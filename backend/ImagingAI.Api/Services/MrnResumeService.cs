using System.Globalization;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ImagingAI.Api.Services;

public interface IMrnWorkflowRepository
{
    Task<IMrnWorkflowSession?> OpenAsync(long exceptionId, CancellationToken token);
}
public interface IMrnWorkflowSession : IAsyncDisposable
{
    long ReportId { get; }
    long ReviewerId { get; }
    string ExceptionStatus { get; }
    string ProcessingStatus { get; }
    int Blockers { get; }
    string Storage { get; }
    IReadOnlyList<MrnCorrectionMetadata> Corrections { get; }
    IReadOnlyList<MrnRevalidationRecord> Validations { get; }
    IReadOnlyList<MrnResumeRun> Runs { get; }
    Task<MrnEvidence?> EvidenceAsync(CancellationToken token);
    Task<bool> PatientMatchesAsync(string mrn, CancellationToken token);
    Task CommitValidationAsync(MrnRevalidationRecord record, CancellationToken token);
    Task CommitRunAsync(MrnResumeRun run, string state, string? exceptionCode, CancellationToken token);
}

public sealed class MrnWorkflowRepository(ImagingAiDbContext database, MrnEvidenceService evidence,
    IConfiguration configuration) : IMrnWorkflowRepository
{
    public async Task<IMrnWorkflowSession?> OpenAsync(long id, CancellationToken token)
    {
        await database.Database.OpenConnectionAsync(token);
        return await OpenOnConnectionAsync((NpgsqlConnection)database.Database.GetDbConnection(), id, token);
    }
    internal async Task<IMrnWorkflowSession?> OpenOnConnectionAsync(NpgsqlConnection connection, long id, CancellationToken token)
    {
        var reviewer = configuration.GetValue<long>("MrnReview:ReviewerId");
        var allowed = configuration.GetSection("MrnReview:AllowedSyntheticCaseIds").Get<long[]>() ?? [];
        if (reviewer <= 0 || allowed.Length == 0) return null;
        // Exception inserts can race with validation; serialize the blocker predicate with the queue decision.
        var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        try
        {
            await using (var limits = new NpgsqlCommand("SET LOCAL lock_timeout='5s'", connection, tx)) await limits.ExecuteNonQueryAsync(token);
            await using var lookup = new NpgsqlCommand("""
                SELECT e.report_id FROM system.exception_queue e JOIN clinical.clinical_report r ON r.report_id=e.report_id
                WHERE e.exception_id=@id AND e.exception_category='MRN_MISMATCH' AND NOT r.is_deleted
                AND e.report_id=ANY(@allowed)
                AND EXISTS(SELECT 1 FROM system.user_account WHERE user_id=@reviewer AND is_active)
                """, connection, tx);
            lookup.Parameters.AddWithValue("id", id); lookup.Parameters.AddWithValue("allowed", allowed); lookup.Parameters.AddWithValue("reviewer", reviewer);
            if (await lookup.ExecuteScalarAsync(token) is not long report) { await tx.DisposeAsync(); return null; }
            string processing, storage, state;
            await using (var command = new NpgsqlCommand("SELECT processing_status, storage_uri FROM clinical.clinical_report WHERE report_id=@report AND NOT is_deleted FOR UPDATE", connection, tx))
            {
                command.Parameters.AddWithValue("report", report);
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token)) throw new InvalidDataException();
                processing = reader.GetString(0); storage = reader.GetString(1);
            }
            await using (var command = new NpgsqlCommand("SELECT status FROM system.exception_queue WHERE exception_id=@id AND report_id=@report AND exception_category='MRN_MISMATCH' FOR UPDATE", connection, tx))
            {
                command.Parameters.AddWithValue("report", report); command.Parameters.AddWithValue("id", id);
                state = await command.ExecuteScalarAsync(token) as string ?? throw new InvalidDataException();
            }
            var corrections = new List<MrnCorrectionMetadata>(); var validations = new List<MrnRevalidationRecord>(); var runs = new List<MrnResumeRun>();
            await using (var history = new NpgsqlCommand("""
                SELECT review_comment, data_version, reviewer_id, review_status FROM system.review_record
                WHERE report_id=@report AND review_status IN ('MRN_CORRECTION_SAVED','MRN_REVALIDATION_PASSED',
                    'MRN_REVALIDATION_FAILED','MRN_RESUME_STARTED','MRN_RESUME_FAILED','MRN_RESUME_BLOCKED','MRN_RESUME_PUBLISHED',
                    'MRN_RESUME_RETRY','MRN_RESUME_STOPPED','MRN_RESUME_REATTEMPT')
                """, connection, tx))
            {
                history.Parameters.AddWithValue("report", report);
                await using var reader = await history.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    if (reader.IsDBNull(0)) throw new InvalidDataException();
                    var json = reader.GetString(0); var marker = reader.GetString(3);
                    if (marker == "MRN_CORRECTION_SAVED")
                    {
                        var c = JsonSerializer.Deserialize<MrnCorrectionMetadata>(json, MrnCorrectionJson.Options) ?? throw new InvalidDataException();
                        if (c.Schema != "mrn_correction/v1" || c.ReportId != report || c.Version < 1 ||
                            c.Version != reader.GetInt32(1) || c.ReviewerId != reader.GetInt64(2) || c.ReviewerId != reviewer)
                            throw new InvalidDataException();
                        if (c.ExceptionId == id) corrections.Add(c);
                    }
                    else if (marker.StartsWith("MRN_REVALIDATION_", StringComparison.Ordinal))
                    {
                        var v = JsonSerializer.Deserialize<MrnRevalidationRecord>(json, MrnCorrectionJson.Options) ?? throw new InvalidDataException();
                        if (v.Schema != "mrn_revalidation/v2" || v.ReportId != report || v.Request.ExpectedCorrectionVersion != reader.GetInt32(1) ||
                            v.ReviewerId != reader.GetInt64(2) || v.ReviewerId != reviewer ||
                            v.Result.RequestId != v.Request.RequestId || v.Result.CloudUploadAllowed ||
                            v.Result.Passed != (marker == "MRN_REVALIDATION_PASSED")) throw new InvalidDataException();
                        if (v.ExceptionId == id) validations.Add(v);
                    }
                    else
                    {
                        var r = JsonSerializer.Deserialize<MrnResumeRun>(json, MrnCorrectionJson.Options) ?? throw new InvalidDataException();
                        if (r.Schema != "mrn_resume_run/v1" || r.ReportId != report || r.Version != reader.GetInt32(1) ||
                            reader.GetInt64(2) != reviewer || r.Status != marker || r.Sequence < 1)
                            throw new InvalidDataException();
                        if (r.ExceptionId == id) runs.Add(r);
                    }
                }
            }
            if (corrections.Select(c => c.Version).Distinct().Count() != corrections.Count) throw new InvalidDataException();
            await using var blockers = new NpgsqlCommand("SELECT count(*) FROM system.exception_queue WHERE report_id=@report AND exception_id<>@id AND status NOT IN ('RESOLVED','UNRESOLVABLE')", connection, tx);
            blockers.Parameters.AddWithValue("report", report); blockers.Parameters.AddWithValue("id", id);
            var count = checked((int)(long)(await blockers.ExecuteScalarAsync(token))!);
            return new Session(connection, tx, evidence, id, report, reviewer, processing, storage, state, count, corrections, validations, runs);
        }
        catch { await tx.DisposeAsync(); throw; }
    }
    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction tx, MrnEvidenceService evidence,
        long id, long report, long reviewer, string processing, string storage, string state, int blockers,
        IReadOnlyList<MrnCorrectionMetadata> corrections, IReadOnlyList<MrnRevalidationRecord> validations,
        IReadOnlyList<MrnResumeRun> runs) : IMrnWorkflowSession
    {
        public long ReportId => report; public long ReviewerId => reviewer;
        public string ExceptionStatus => state; public string ProcessingStatus => processing; public string Storage => storage;
        public int Blockers => blockers; public IReadOnlyList<MrnCorrectionMetadata> Corrections => corrections;
        public IReadOnlyList<MrnRevalidationRecord> Validations => validations; public IReadOnlyList<MrnResumeRun> Runs => runs;
        public Task<MrnEvidence?> EvidenceAsync(CancellationToken token) => evidence.ReadInTransactionAsync(id, connection, tx, token);
        public async Task<bool> PatientMatchesAsync(string mrn, CancellationToken token)
        {
            await using var c = new NpgsqlCommand("""
                SELECT r.patient_id IS NULL OR EXISTS(SELECT 1 FROM clinical.patient p WHERE p.patient_id=r.patient_id AND p.mrn=@mrn)
                FROM clinical.clinical_report r WHERE r.report_id=@report
                """, connection, tx);
            c.Parameters.AddWithValue("report", report); c.Parameters.AddWithValue("mrn", mrn);
            return await c.ExecuteScalarAsync(token) is true;
        }
        private async Task AppendAsync(string marker, string json, int version, CancellationToken token)
        {
            await using var c = new NpgsqlCommand("""
                INSERT INTO system.review_record(report_id,reviewer_id,review_status,reviewed_at,review_comment,data_version)
                VALUES(@report,@reviewer,@marker,LOCALTIMESTAMP,@json,@version);
                INSERT INTO system.audit_trail(report_id,actor_id,action,after_json,data_version)
                VALUES(@report,@reviewer,@marker,@json,@version);
                INSERT INTO system.processing_log(report_id,status) VALUES(@report,@marker);
                """, connection, tx);
            c.Parameters.AddWithValue("report", report); c.Parameters.AddWithValue("reviewer", reviewer);
            c.Parameters.AddWithValue("marker", marker); c.Parameters.AddWithValue("json", json); c.Parameters.AddWithValue("version", version);
            if (await c.ExecuteNonQueryAsync(token) != 3) throw new InvalidDataException();
        }
        private async Task ChangeReportAsync(string target, CancellationToken token)
        {
            await using var c = new NpgsqlCommand("UPDATE clinical.clinical_report SET processing_status=@target WHERE report_id=@report AND processing_status=@previous AND NOT is_deleted", connection, tx);
            c.Parameters.AddWithValue("target", target); c.Parameters.AddWithValue("report", report); c.Parameters.AddWithValue("previous", processing);
            if (await c.ExecuteNonQueryAsync(token) != 1) throw new MrnWorkflowException(409, "STATE_CONFLICT");
        }
        public async Task CommitValidationAsync(MrnRevalidationRecord record, CancellationToken token)
        {
            if (processing != "MRN_REVIEW_REQUIRED" || state != "FIXED_PENDING_REVALIDATION") throw new MrnWorkflowException(409, "STATE_CONFLICT");
            await AppendAsync(record.Result.Passed ? "MRN_REVALIDATION_PASSED" : "MRN_REVALIDATION_FAILED",
                JsonSerializer.Serialize(record, MrnCorrectionJson.Options), record.Request.ExpectedCorrectionVersion, token);
            await using var c = new NpgsqlCommand("UPDATE system.exception_queue SET status=@state WHERE exception_id=@id AND report_id=@report AND status='FIXED_PENDING_REVALIDATION'", connection, tx);
            c.Parameters.AddWithValue("state", record.Result.ExceptionStatus); c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("report", report);
            if (await c.ExecuteNonQueryAsync(token) != 1) throw new MrnWorkflowException(409, "STATE_CONFLICT");
            if (record.Result.Passed) await ChangeReportAsync("MRN_REVALIDATED", token);
            await tx.CommitAsync(token);
        }
        public async Task CommitRunAsync(MrnResumeRun run, string target, string? exceptionCode, CancellationToken token)
        {
            await AppendAsync(run.Status, JsonSerializer.Serialize(run, MrnCorrectionJson.Options), run.Version, token);
            await ChangeReportAsync(target, token);
            if (exceptionCode is not null)
            {
                var description = $"MRN 背景恢復未完成（工作 {run.JobId:N}，代碼 {exceptionCode}）。";
                await using var c = new NpgsqlCommand("""
                    INSERT INTO system.exception_queue(report_id,exception_category,description,status)
                    SELECT @report,@code,@description,'OPEN' WHERE NOT EXISTS
                    (SELECT 1 FROM system.exception_queue WHERE report_id=@report AND exception_category=@code AND description=@description)
                    """, connection, tx);
                c.Parameters.AddWithValue("report", report); c.Parameters.AddWithValue("code", exceptionCode); c.Parameters.AddWithValue("description", description);
                await c.ExecuteNonQueryAsync(token);
            }
            await tx.CommitAsync(token);
        }
        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }
}

public sealed class MrnSourceCheckpoint(IWebHostEnvironment environment)
{
    public string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InvalidDataException();
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "PrivateUploads")) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        for (var current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return path;
    }
    public async Task<IReadOnlyList<MrnSourceFile>> CaptureAsync(long report, Guid attempt, string storage, MrnCorrectionMetadata correction, CancellationToken token)
    {
        var prefix = $"PrivateUploads/ocr/{report}/{attempt:N}";
        var paths = new List<string> { storage, $"PrivateUploads/{report}.mrn.protected", $"PrivateUploads/ocr/{report}/current.txt",
            $"{prefix}/ocr_result.json", $"{prefix}/extracted_fields.json",
            $"PrivateUploads/mrn-corrections/{report}/{correction.ExceptionId}/{correction.ProtectedReference}" };
        using var raw = JsonDocument.Parse(await File.ReadAllBytesAsync(Resolve($"{prefix}/ocr_result.json"), token));
        var count = raw.RootElement.GetProperty("total_pages").GetInt32();
        if (count < 1 || raw.RootElement.GetProperty("pages").GetArrayLength() != count) throw new InvalidDataException();
        if (Path.GetExtension(storage) != ".pdf") paths.Add($"{prefix}/source_image.pdf");
        for (var i = 1; i <= count; i++) paths.Add($"{prefix}/pages/page_{i:D3}.png");
        var result = new List<MrnSourceFile>();
        foreach (var path in paths)
        {
            await using var file = File.OpenRead(Resolve(path));
            result.Add(new(path, Convert.ToHexString(await SHA256.HashDataAsync(file, token))));
        }
        return result;
    }
    public async Task<SourceLease> LeaseAsync(MrnResumeWork work, CancellationToken token)
    {
        var lease = new SourceLease(work.Files);
        try
        {
            if (work.Files.Count < 7 || work.Files.Select(f => f.RelativePath).Distinct().Count() != work.Files.Count) throw new InvalidDataException();
            foreach (var file in work.Files) lease.Streams.Add(new FileStream(Resolve(file.RelativePath), FileMode.Open, FileAccess.Read, FileShare.Read));
            await lease.VerifyAsync(token); return lease;
        }
        catch { await lease.DisposeAsync(); throw; }
    }
    public sealed class SourceLease(IReadOnlyList<MrnSourceFile> files) : IAsyncDisposable
    {
        internal readonly List<FileStream> Streams = [];
        public async Task VerifyAsync(CancellationToken token)
        {
            for (var i = 0; i < Streams.Count; i++)
            { Streams[i].Position = 0; if (Convert.ToHexString(await SHA256.HashDataAsync(Streams[i], token)) != files[i].Sha256) throw new InvalidDataException(); }
        }
        public async ValueTask DisposeAsync() { foreach (var stream in Streams) await stream.DisposeAsync(); }
    }
}

public sealed class MrnResumeLeaseLostException : IOException
{
    public MrnResumeLeaseLostException(string kind, Guid? runId, string? sqlState = null)
        : base("MRN resume execution lease was lost")
    {
        Kind = kind;
        RunId = runId;
        SqlState = sqlState;
    }

    public string Kind { get; }
    public Guid? RunId { get; }
    public string? SqlState { get; }
}

public sealed class MrnResumeService(IMrnWorkflowRepository repository, IProtectedMrnCorrectionStore store,
    MrnSourceCheckpoint checkpoint, IWebHostEnvironment environment, ILogger<MrnResumeService> logger,
    TimeProvider? timeProvider = null)
{
    private const int MaxAttempts = 3;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<bool> ProcessAsync(long exceptionId,
        Func<MrnResumeWork, ProtectedMrnCorrection, string, CancellationToken, Task> execute, CancellationToken token,
        Func<Guid?, CancellationToken, Task>? checkOwner = null)
    {
        MrnResumeWork? work = null; MrnResumeRun? run = null;
        MrnSourceCheckpoint.SourceLease? lease = null;
        Task CheckOwnerAsync() => checkOwner?.Invoke(run?.RunId, token) ?? Task.CompletedTask;
        try
        {
            ProtectedMrnCorrection saved;
            await using (var session = await repository.OpenAsync(exceptionId, token) ?? throw new InvalidDataException())
            {
                if (session.ProcessingStatus is not ("MRN_REVALIDATED" or "MRN_DEID_PROCESSING")) return false;
                work = SelectWork(session);
                var prior = LatestRun(session, work);
                var attempts = session.Runs.Where(r => r.JobId == work.JobId && r.RunId == prior?.RunId &&
                        r.Status is "MRN_RESUME_STARTED" or "MRN_RESUME_REATTEMPT")
                    .Max(r => r.Attempt) ?? 0;
                var decisions = session.Runs.Where(r => r.JobId == work.JobId &&
                    r.RunId == prior?.RunId && r.Attempt == attempts).ToArray();
                if (decisions.Any(r => r.Status == "MRN_RESUME_STOPPED")) return false;
                var retry = decisions.SingleOrDefault(r => r.Status == "MRN_RESUME_RETRY");
                if (retry?.RetryAt > clock.GetUtcNow()) return false;

                saved = await ValidateAsync(session, work, token);
                lease = await checkpoint.LeaseAsync(work, token);
                if (attempts > 0 && retry is null)
                {
                    run = prior;
                    var now = clock.GetUtcNow();
                    var exhausted = attempts >= MaxAttempts;
                    await CheckOwnerAsync();
                    await session.CommitRunAsync(prior! with
                    {
                        Status = exhausted ? "MRN_RESUME_STOPPED" : "MRN_RESUME_RETRY",
                        At = now,
                        Attempt = attempts,
                        Reason = exhausted ? "ATTEMPT_LIMIT" : "INCOMPLETE_EXECUTION",
                        RetryAt = exhausted ? null : now.AddSeconds(attempts == 1 ? 5 : 15)
                    }, "MRN_DEID_PROCESSING", null, token);
                    return false;
                }

                if (session.ProcessingStatus == "MRN_DEID_PROCESSING" && prior is not null &&
                    await TryArtifactAsync(work, prior, token) is not null)
                {
                    run = prior with { Status = "MRN_RESUME_REATTEMPT", At = clock.GetUtcNow(), Attempt = attempts + 1 };
                }
                else
                {
                    run = new("mrn_resume_run/v1", work.JobId, Guid.NewGuid(), work.ReportId, work.ExceptionId,
                        work.CorrectionVersion, clock.GetUtcNow(), "MRN_RESUME_STARTED",
                        Sequence: (prior?.Sequence ?? 0) + 1, Attempt: attempts + 1);
                }
                await CheckOwnerAsync();
                await session.CommitRunAsync(run, "MRN_DEID_PROCESSING", null, token);
            }
            await CheckOwnerAsync();
            var directory = RunDirectory(work, run);
            if (await TryArtifactAsync(work, run, token) is null)
            {
                if (Directory.Exists(directory)) throw new InvalidDataException(); // never reuse/overwrite an incomplete attempt
                Directory.CreateDirectory(directory); PrivateJsonFieldsStore.RestrictAttemptDirectory(directory);
                await WithHeartbeatAsync(run.RunId, checkOwner, async executionToken =>
                {
                    await execute(work, saved, directory, executionToken);
                    await PdfOcrWorker.ValidateDeidResultAsync(directory, PageCount(work), executionToken);
                    executionToken.ThrowIfCancellationRequested();
                    await WriteArtifactAsync(work, run, executionToken);
                }, token);
            }
            string? artifact = null;
            await WithHeartbeatAsync(run.RunId, checkOwner, async executionToken =>
            {
                await lease.VerifyAsync(executionToken);
                artifact = await TryArtifactAsync(work, run, executionToken) ?? throw new InvalidDataException();
            }, token);
            await CheckOwnerAsync();
            await using (var publish = await repository.OpenAsync(exceptionId, token) ?? throw new InvalidDataException())
            {
                var currentWork = SelectWork(publish);
                await ValidateAsync(publish, currentWork, token);
                if (publish.ProcessingStatus != "MRN_DEID_PROCESSING" || currentWork.JobId != work.JobId ||
                    currentWork.Files.Count != work.Files.Count || !currentWork.Files.SequenceEqual(work.Files) ||
                    LatestRun(publish, currentWork)?.RunId != run.RunId)
                    throw new MrnWorkflowException(409, "STALE_RUN");
                await lease.VerifyAsync(token);
                await CheckOwnerAsync();
                await publish.CommitRunAsync(run with { Status = "MRN_RESUME_PUBLISHED", At = clock.GetUtcNow(),
                    ArtifactSha256 = artifact }, "PENDING_REVIEW", null, token);
            }
            try { await PublishPointerAsync(work, run, token, checkOwner); }
            catch (MrnResumeLeaseLostException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { logger.LogWarning("MRN published pointer needs repair for report {ReportId}", work.ReportId); }
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (MrnResumeLeaseLostException) { throw; }
        catch (Exception e) when (e is IOException or InvalidDataException or JsonException or InvalidOperationException or
            UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or KeyNotFoundException or
            TimeoutException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or MrnWorkflowException)
        {
            logger.LogWarning("MRN resume report {ReportId} stopped ({Type})", work?.ReportId, e.GetType().Name);
            await CheckOwnerAsync();
            await using var failed = await repository.OpenAsync(exceptionId, token);
            if (failed is null || failed.ProcessingStatus is not ("MRN_REVALIDATED" or "MRN_DEID_PROCESSING")) return false;
            if (run is not null && LatestRun(failed, work!)?.RunId != run.RunId) return false; // old executions cannot change new state
            var blocked = run is null || e is MrnWorkflowException;
            var failure = run ?? new MrnResumeRun("mrn_resume_run/v1", work?.JobId ?? Guid.Empty, Guid.NewGuid(), failed.ReportId,
                exceptionId, failed.Corrections.Select(c => c.Version).DefaultIfEmpty(1).Max(), clock.GetUtcNow(), "MRN_RESUME_BLOCKED");
            var exceptionCode = blocked ? e switch
            {
                MrnWorkflowException { Code: "SOURCE_CONFLICT" or "EVIDENCE_INTEGRITY_FAILED" or "STALE_RUN" } => "OCR_CANDIDATE_CONFLICT",
                _ => "MRN_RESUME_BLOCKED"
            } : "DEID_VERIFICATION_FAILED";
            await CheckOwnerAsync();
            await failed.CommitRunAsync(failure with { Status = blocked ? "MRN_RESUME_BLOCKED" : "MRN_RESUME_FAILED", At = clock.GetUtcNow() },
                blocked ? "MRN_REVIEW_REQUIRED" : "DEID_FAILED", exceptionCode, token);
            return true;
        }
        finally { if (lease is not null) await lease.DisposeAsync(); }
    }

    private static async Task WithHeartbeatAsync(Guid runId, Func<Guid?, CancellationToken, Task>? checkOwner,
        Func<CancellationToken, Task> action, CancellationToken token)
    {
        if (checkOwner is null)
        {
            await action(token);
            return;
        }

        await checkOwner(runId, token);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var monitoring = new CancellationTokenSource();
        var heartbeat = MonitorAsync();
        try
        {
            await action(execution.Token);
        }
        finally
        {
            monitoring.Cancel();
            await heartbeat;
        }
        await checkOwner(runId, token);

        async Task MonitorAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            try
            {
                while (await timer.WaitForNextTickAsync(monitoring.Token))
                    await checkOwner(runId, token);
            }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested || token.IsCancellationRequested) { }
            catch (MrnResumeLeaseLostException)
            {
                execution.Cancel();
                throw;
            }
        }
    }

    internal static MrnResumeWork SelectWork(IMrnWorkflowSession session)
    {
        var latest = session.Corrections.OrderByDescending(c => c.Version).FirstOrDefault() ?? throw new InvalidDataException();
        var records = session.Validations.Where(v => v.Result.Passed && v.Work is not null &&
            v.Request.ExpectedCorrectionVersion == latest.Version && v.Request.CorrectionRequestId == latest.RequestId).ToArray();
        if (records.Length != 1) throw new InvalidDataException();
        var work = records[0].Work!;
        if (work.JobId == Guid.Empty || records[0].Result.ResumeJobId != work.JobId || !records[0].Result.ResumeQueued ||
            records[0].Result.CloudUploadAllowed || work.ReportId != session.ReportId || work.ExceptionId != latest.ExceptionId ||
            work.CorrectionVersion != latest.Version || work.CorrectionRequestId != latest.RequestId ||
            work.OcrAttemptId != latest.OcrAttemptId || work.OriginalSha256 != latest.OriginalSha256 ||
            work.CandidateSha256 != latest.CandidateSha256 || work.CorrectionSha256 != latest.ProtectedSha256 ||
            string.IsNullOrWhiteSpace(work.StorageUri) || !work.Files.Any(f => f.RelativePath == work.StorageUri))
            throw new InvalidDataException();
        return work;
    }
    private async Task<ProtectedMrnCorrection> ValidateAsync(IMrnWorkflowSession session, MrnResumeWork work, CancellationToken token)
    {
        if (session.ExceptionStatus != "RESOLVED" || session.Blockers != 0 || !WorkMatches(SelectWork(session), work) ||
            !string.Equals(session.Storage, work.StorageUri, StringComparison.Ordinal))
            throw new MrnWorkflowException(409, "RESUME_BLOCKED");
        var metadata = session.Corrections.Single(c => c.Version == work.CorrectionVersion);
        var saved = await store.ReadAsync(metadata, token);
        var evidence = await session.EvidenceAsync(token) ?? throw new MrnWorkflowException(409, "SOURCE_CONFLICT");
        MrnRevalidationService.ValidateConfirmed(saved, metadata, evidence);
        if (saved.Request.Reason == MrnCorrectionReason.DOCUMENT_CASE_MISMATCH ||
            !await session.PatientMatchesAsync(saved.Request.ConfirmedMrn, token)) throw new MrnWorkflowException(409, "RESUME_BLOCKED");
        return saved;
    }
    private static bool WorkMatches(MrnResumeWork left, MrnResumeWork right) =>
        left.JobId == right.JobId && left.ReportId == right.ReportId && left.StorageUri == right.StorageUri &&
        left.ExceptionId == right.ExceptionId && left.CorrectionVersion == right.CorrectionVersion &&
        left.CorrectionRequestId == right.CorrectionRequestId && left.OcrAttemptId == right.OcrAttemptId &&
        left.OriginalSha256 == right.OriginalSha256 && left.CandidateSha256 == right.CandidateSha256 &&
        left.CorrectionSha256 == right.CorrectionSha256 && left.Files.SequenceEqual(right.Files);
    private static MrnResumeRun? LatestRun(IMrnWorkflowSession session, MrnResumeWork work)
    {
        var starts = session.Runs.Where(r => r.JobId == work.JobId && r.Status == "MRN_RESUME_STARTED").ToArray();
        if (starts.Select(r => r.Sequence).Distinct().Count() != starts.Length) throw new InvalidDataException();
        return starts.OrderByDescending(r => r.Sequence).FirstOrDefault();
    }
    private string RunDirectory(MrnResumeWork work, MrnResumeRun run) => Path.Combine(environment.ContentRootPath,
        "PrivateUploads", "ocr", work.ReportId.ToString(CultureInfo.InvariantCulture), work.OcrAttemptId.ToString("N"), "deid", run.RunId.ToString("N"));
    private static int PageCount(MrnResumeWork work) => work.Files.Count(f => f.RelativePath.StartsWith(
        $"PrivateUploads/ocr/{work.ReportId}/{work.OcrAttemptId:N}/pages/", StringComparison.Ordinal));
    private sealed record Artifact(string Schema, Guid JobId, Guid RunId, IReadOnlyList<MrnSourceFile> Files);
    private async Task WriteArtifactAsync(MrnResumeWork work, MrnResumeRun run, CancellationToken token)
    {
        var dir = RunDirectory(work, run);
        var names = new List<string> { "result_deidentified.json" };
        for (var i = 1; i <= PageCount(work); i++) names.Add($"result_deidentified_pages/page_{i:D3}.png");
        var files = new List<MrnSourceFile>();
        foreach (var name in names)
        { await using var file = File.OpenRead(Path.Combine(dir, name)); files.Add(new(name, Convert.ToHexString(await SHA256.HashDataAsync(file, token)))); }
        await using var output = new FileStream(Path.Combine(dir, "resume-artifact.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, new Artifact("mrn_resume_artifact/v1", work.JobId, run.RunId, files), MrnCorrectionJson.Options, token);
    }
    private async Task<string?> TryArtifactAsync(MrnResumeWork work, MrnResumeRun run, CancellationToken token)
    {
        try
        {
            var dir = RunDirectory(work, run); var file = Path.Combine(dir, "resume-artifact.json");
            if (!File.Exists(file)) return null;
            var bytes = await File.ReadAllBytesAsync(file, token);
            var artifact = JsonSerializer.Deserialize<Artifact>(bytes, MrnCorrectionJson.Options) ?? throw new InvalidDataException();
            var expected = new HashSet<string> { "result_deidentified.json" };
            for (var i = 1; i <= PageCount(work); i++) expected.Add($"result_deidentified_pages/page_{i:D3}.png");
            if (artifact.Schema != "mrn_resume_artifact/v1" || artifact.JobId != work.JobId || artifact.RunId != run.RunId ||
                artifact.Files.Count != expected.Count || !artifact.Files.Select(f => f.RelativePath).ToHashSet().SetEquals(expected)) throw new InvalidDataException();
            foreach (var item in artifact.Files)
            {
                await using var input = File.OpenRead(Path.Combine(dir, item.RelativePath));
                if (Convert.ToHexString(await SHA256.HashDataAsync(input, token)) != item.Sha256) throw new InvalidDataException();
            }
            await PdfOcrWorker.ValidateDeidResultAsync(dir, PageCount(work), token);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException) { return null; }
    }
    private async Task PublishPointerAsync(MrnResumeWork work, MrnResumeRun run, CancellationToken token,
        Func<Guid?, CancellationToken, Task>? checkOwner = null)
    {
        var dir = Path.GetDirectoryName(RunDirectory(work, run))!;
        var destination = Path.Combine(dir, "current.txt");
        if (File.Exists(destination) && string.Equals(
                (await File.ReadAllTextAsync(destination, token)).Trim(), run.RunId.ToString("N"), StringComparison.Ordinal))
            return;
        var pending = Path.Combine(dir, $".publish-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(pending, run.RunId.ToString("N"), token);
            if (checkOwner is not null) await checkOwner(run.RunId, token);
            token.ThrowIfCancellationRequested();
            File.Move(pending, destination, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    public async Task RepairPublishedAsync(NpgsqlConnection connection, long uploader, long? onlyCase, CancellationToken token,
        Func<long, Guid?, CancellationToken, Task> checkOwner)
    {
        var ids = new List<long>();
        await using (var command = new NpgsqlCommand("""
            SELECT DISTINCT e.exception_id FROM system.exception_queue e JOIN clinical.clinical_report r ON r.report_id=e.report_id
            JOIN system.review_record v ON v.report_id=r.report_id AND v.review_status='MRN_RESUME_PUBLISHED'
            WHERE r.uploaded_by=@uploader AND NOT r.is_deleted AND r.processing_status='PENDING_REVIEW'
            AND e.exception_category='MRN_MISMATCH' AND (@case IS NULL OR r.report_id=@case)
            """, connection))
        {
            command.Parameters.AddWithValue("uploader", uploader);
            command.Parameters.AddWithValue("case", NpgsqlTypes.NpgsqlDbType.Bigint, (object?)onlyCase ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) ids.Add(reader.GetInt64(0));
        }
        foreach (var id in ids)
        {
            try
            {
                await using var session = await repository.OpenAsync(id, token);
                if (session is null || session.ProcessingStatus != "PENDING_REVIEW") continue;
                var work = SelectWork(session); var run = LatestRun(session, work);
                var publication = session.Runs.SingleOrDefault(r => r.JobId == work.JobId && r.RunId == run?.RunId && r.Status == "MRN_RESUME_PUBLISHED");
                if (run is null || publication is null) continue;
                var digest = await TryArtifactAsync(work, run, token);
                if (digest != publication.ArtifactSha256) throw new InvalidDataException();
                await PublishPointerAsync(work, run, token, (runId, ct) => checkOwner(work.ReportId, runId, ct));
            }
            catch (MrnResumeLeaseLostException) { throw; }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            { logger.LogWarning("MRN pointer repair needs review for exception {ExceptionId}", id); }
        }
    }
}
