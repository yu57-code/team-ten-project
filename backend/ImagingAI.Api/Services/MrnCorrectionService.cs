using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ImagingAI.Api.Services;

public interface IMrnCorrectionAccess { bool IsAuthorized(string credential); }

public interface IMrnCorrectionRepository
{
    Task<IMrnCorrectionSession?> BeginAsync(long exceptionId, CancellationToken token);
}

public interface IMrnCorrectionSession : IAsyncDisposable
{
    MrnEvidence Evidence { get; }
    string ExceptionStatus { get; }
    string ProcessingStatus { get; }
    IReadOnlyList<MrnCorrectionMetadata> Corrections { get; }
    Task RecheckEvidenceAsync(CancellationToken token);
    Task CommitAsync(MrnCorrectionMetadata metadata, CancellationToken token);
}

public sealed class MrnCorrectionService(IMrnCorrectionAccess access, IMrnCorrectionRepository repository,
    IProtectedMrnCorrectionStore store)
{
    public async Task<MrnCorrectionResponse> SaveAsync(long exceptionId, MrnCorrectionRequest request,
        string credential, CancellationToken token)
    {
        if (!access.IsAuthorized(credential)) throw new MrnCorrectionException(404, "找不到可處置的異常。");
        request = Validate(request);
        await using var session = await repository.BeginAsync(exceptionId, token)
            ?? throw new MrnCorrectionException(404, "找不到可處置的異常。");
        var evidence = session.Evidence;
        if (session.ProcessingStatus != "MRN_REVIEW_REQUIRED" ||
            session.ExceptionStatus is not ("OPEN" or "FIXED_PENDING_REVALIDATION"))
            throw Conflict();
        CheckEvidence(request, evidence);
        var prior = session.Corrections.Where(c => c.RequestId == request.RequestId).ToArray();
        if (prior.Length > 1) throw Conflict();
        if (prior.Length == 1)
        {
            var saved = await store.ReadAsync(prior[0], token);
            if (saved.ReviewerId != evidence.ReviewerId ||
                JsonSerializer.Serialize(saved.Request, MrnCorrectionJson.Options) !=
                JsonSerializer.Serialize(request, MrnCorrectionJson.Options)) throw Conflict();
            return Result(request.RequestId, prior[0].Version, request.Reason);
        }
        var current = session.Corrections.Count == 0 ? 0 : session.Corrections.Max(c => c.Version);
        if (request.ExpectedCorrectionVersion != current) throw Conflict();
        if (current > 0)
            await store.ReadAsync(session.Corrections.Single(c => c.Version == current), token);
        var next = checked(current + 1);
        var report = long.Parse(evidence.ReportId, CultureInfo.InvariantCulture);
        var savedAt = DateTimeOffset.UtcNow;
        var payload = new ProtectedMrnCorrection(exceptionId, report, next, evidence.ReviewerId, savedAt, request);
        var file = await store.WriteAsync(payload, token);
        await session.RecheckEvidenceAsync(token);
        var metadata = new MrnCorrectionMetadata("mrn_correction/v1", exceptionId, report,
            request.RequestId, next, evidence.ReviewerId, savedAt, evidence.OcrAttemptId,
            request.OriginalSha256, request.CandidateSha256, request.Reason, file.Reference, file.Sha256);
        await session.CommitAsync(metadata, token);
        return Result(request.RequestId, next, request.Reason);
    }

    private static MrnCorrectionResponse Result(Guid id, int version, MrnCorrectionReason reason) =>
        new(true, reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH
            ? "文件與病例不符的紀錄已保存；需確認來源，不可據此恢復處理。"
            : "人工修正已保存，等待重新驗證；病例仍維持阻擋。", id, version,
            SourceConfirmationRequired: reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH);

    internal static MrnCorrectionException Conflict() => new(409, "來源、狀態或修正版本已變更，請重新載入。");

    private static MrnCorrectionRequest Validate(MrnCorrectionRequest request)
    {
        if (request is null || request.RequestId == Guid.Empty || request.OcrAttemptId == Guid.Empty ||
            request.ExpectedCorrectionVersion < 0 || !Enum.IsDefined(request.Reason) ||
            request.ConfirmedMrn is null || !Regex.IsMatch(request.ConfirmedMrn,
                @"\A[\p{L}\p{Nd}][\p{L}\p{Nd}._/-]{0,49}\z") ||
            string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000 ||
            request.EvidenceReferences is null || request.EvidenceReferences.Count is < 1 or > 100 ||
            request.EvidenceReferences.Any(r => r is null || r.Page < 1 || string.IsNullOrWhiteSpace(r.BlockId) || r.BlockId.Length > 200) ||
            request.OriginalSha256 is null || !Regex.IsMatch(request.OriginalSha256, @"\A[0-9a-fA-F]{64}\z") ||
            request.CandidateSha256 is null || !Regex.IsMatch(request.CandidateSha256, @"\A[0-9a-fA-F]{64}\z"))
            throw new MrnCorrectionException(400, "修正內容、MRN 格式或來源參照無效。");
        return request with
        {
            OriginalSha256 = request.OriginalSha256.ToUpperInvariant(),
            CandidateSha256 = request.CandidateSha256.ToUpperInvariant(),
            EvidenceReferences = request.EvidenceReferences.Distinct().OrderBy(r => r.Page)
                .ThenBy(r => r.BlockId, StringComparer.Ordinal).ToArray()
        };
    }

    private static void CheckEvidence(MrnCorrectionRequest request, MrnEvidence evidence)
    {
        if (request.OcrAttemptId != evidence.OcrAttemptId ||
            !string.Equals(request.OriginalSha256, evidence.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.CandidateSha256, evidence.CandidateSha256, StringComparison.OrdinalIgnoreCase) ||
            request.EvidenceReferences.Any(r => !evidence.Candidates.Any(c => c.Page == r.Page && c.BlockId == r.BlockId)))
            throw Conflict();
        // The reason must agree with the observed source; format alone is never identity verification.
        if (request.Reason == MrnCorrectionReason.UPLOAD_MRN_INPUT_ERROR &&
            !evidence.Candidates.Any(c => c.Value == request.ConfirmedMrn) ||
            request.Reason == MrnCorrectionReason.OCR_RECOGNITION_ERROR &&
            request.ConfirmedMrn != evidence.UploadedMrn)
            throw new MrnCorrectionException(400, "確認值與修正原因或來源證據不一致。");
    }
}

public sealed class NpgsqlMrnCorrectionRepository(ImagingAiDbContext database, MrnEvidenceService evidence)
    : IMrnCorrectionRepository
{
    public async Task<IMrnCorrectionSession?> BeginAsync(long exceptionId, CancellationToken token)
    {
        await database.Database.OpenConnectionAsync(token);
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        var transaction = await connection.BeginTransactionAsync(token);
        try
        {
            await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'", connection, transaction))
                await limits.ExecuteNonQueryAsync(token);
            // Authorize the case before acquiring a write lock or inspecting correction records.
            var initial = await evidence.ReadInTransactionAsync(exceptionId, connection, transaction, token);
            if (initial is null) { await transaction.DisposeAsync(); return null; }
            var report = long.Parse(initial.ReportId, CultureInfo.InvariantCulture);
            await using var reportLock = new NpgsqlCommand("""
                SELECT processing_status FROM clinical.clinical_report
                WHERE report_id=@report AND NOT is_deleted FOR UPDATE
                """, connection, transaction);
            reportLock.Parameters.AddWithValue("report", report);
            var status = await reportLock.ExecuteScalarAsync(token) as string ?? throw MrnCorrectionService.Conflict();
            await using var exceptionLock = new NpgsqlCommand("""
                SELECT status FROM system.exception_queue WHERE exception_id=@id
                  AND report_id=@report AND exception_category='MRN_MISMATCH' FOR UPDATE
                """, connection, transaction);
            exceptionLock.Parameters.AddWithValue("id", exceptionId);
            exceptionLock.Parameters.AddWithValue("report", report);
            var exceptionStatus = await exceptionLock.ExecuteScalarAsync(token) as string ?? throw MrnCorrectionService.Conflict();
            var locked = await evidence.ReadInTransactionAsync(exceptionId, connection, transaction, token)
                ?? throw MrnCorrectionService.Conflict();
            var corrections = new List<MrnCorrectionMetadata>();
            await using var history = new NpgsqlCommand("""
                SELECT review_comment, data_version, reviewer_id FROM system.review_record
                WHERE report_id=@report AND review_status='MRN_CORRECTION_SAVED'
                """, connection, transaction);
            history.Parameters.AddWithValue("report", report);
            await using (var reader = await history.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token))
                {
                    var entry = JsonSerializer.Deserialize<MrnCorrectionMetadata>(reader.GetString(0), MrnCorrectionJson.Options)
                        ?? throw new InvalidDataException();
                    if (entry.Schema != "mrn_correction/v1" || entry.ReportId != report || entry.Version <= 0 ||
                        entry.Version != reader.GetInt32(1) || entry.ReviewerId != reader.GetInt64(2))
                        throw new InvalidDataException();
                    if (entry.ExceptionId == exceptionId) corrections.Add(entry);
                }
            if (corrections.Select(c => c.Version).Distinct().Count() != corrections.Count)
                throw new InvalidDataException();
            return new Session(connection, transaction, evidence, exceptionId, locked, exceptionStatus, status, corrections);
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction,
        MrnEvidenceService evidenceService, long exceptionId, MrnEvidence evidence,
        string exceptionStatus, string processingStatus, IReadOnlyList<MrnCorrectionMetadata> corrections)
        : IMrnCorrectionSession
    {
        public MrnEvidence Evidence => evidence;
        public string ExceptionStatus => exceptionStatus;
        public string ProcessingStatus => processingStatus;
        public IReadOnlyList<MrnCorrectionMetadata> Corrections => corrections;
        public async Task RecheckEvidenceAsync(CancellationToken token)
        {
            var latest = await evidenceService.ReadInTransactionAsync(exceptionId, connection, transaction, token);
            if (latest is null || latest.ReviewerId != evidence.ReviewerId ||
                latest.OcrAttemptId != evidence.OcrAttemptId || latest.OriginalSha256 != evidence.OriginalSha256 ||
                latest.CandidateSha256 != evidence.CandidateSha256) throw MrnCorrectionService.Conflict();
        }

        public async Task CommitAsync(MrnCorrectionMetadata metadata, CancellationToken token)
        {
            var json = JsonSerializer.Serialize(metadata, MrnCorrectionJson.Options);
            // No MRN or free text in these records. All writes share one transaction.
            await using var command = new NpgsqlCommand("""
                INSERT INTO system.review_record
                  (report_id,reviewer_id,review_status,reviewed_at,review_comment,data_version)
                VALUES (@report,@reviewer,'MRN_CORRECTION_SAVED',LOCALTIMESTAMP,@json,@version);
                INSERT INTO system.audit_trail (report_id,actor_id,action,after_json,data_version)
                VALUES (@report,@reviewer,'MRN_CORRECTION_SAVED',@json,@version);
                INSERT INTO system.processing_log (report_id,status)
                VALUES (@report,'MRN_CORRECTION_SAVED');
                UPDATE system.exception_queue SET status='FIXED_PENDING_REVALIDATION',assigned_user_id=@reviewer
                WHERE exception_id=@id AND report_id=@report AND status=@previous;
                """, connection, transaction);
            command.Parameters.AddWithValue("report", metadata.ReportId);
            command.Parameters.AddWithValue("reviewer", metadata.ReviewerId);
            command.Parameters.AddWithValue("json", json);
            command.Parameters.AddWithValue("version", metadata.Version);
            command.Parameters.AddWithValue("id", metadata.ExceptionId);
            command.Parameters.AddWithValue("previous", exceptionStatus);
            if (await command.ExecuteNonQueryAsync(token) != 4) throw MrnCorrectionService.Conflict();
            await transaction.CommitAsync(token);
        }
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
