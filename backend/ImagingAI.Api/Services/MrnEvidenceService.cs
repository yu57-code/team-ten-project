using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ImagingAI.Api.Services;

public sealed class MrnEvidenceService(ImagingAiDbContext database, IConfiguration configuration,
    IWebHostEnvironment environment, IDataProtectionProvider protection) : IMrnCorrectionAccess
{
    internal bool IsCaseAllowed(long[] allowed, bool allowAll) => allowAll || allowed.Length > 0;

    internal bool AllowAllInDevelopment => environment.IsDevelopment() &&
        configuration.GetValue("MrnReview:AllowAllInDevelopment", false);

    public bool IsAuthorized(string token)
    {
        var secret = configuration["MrnReview:TestCredential"];
        return environment.IsDevelopment() && configuration.GetValue("MrnReview:Enabled", false) &&
            !string.IsNullOrWhiteSpace(secret) && secret.Length >= 32 &&
            CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public async Task<MrnEvidence?> ReadAsync(long exceptionId, CancellationToken cancellationToken)
    {
        var reviewer = configuration.GetValue<long>("MrnReview:ReviewerId");
        var allowAll = AllowAllInDevelopment;
        var allowed = configuration.GetSection("MrnReview:AllowedSyntheticCaseIds").Get<long[]>() ?? [];
        if (reviewer <= 0 || !IsCaseAllowed(allowed, allowAll))
            return null;

        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync(cancellationToken);
        var result = await ReadInTransactionAsync(exceptionId, connection, transaction, cancellationToken);
        if (result is not null)
        {
            // Same read-only snapshot as the evidence: never substitute zero on query failure.
            await using var versions = new NpgsqlCommand("""
                SELECT review_comment, data_version, reviewer_id FROM system.review_record
                WHERE report_id=@report AND review_status='MRN_CORRECTION_SAVED'
                """, connection, transaction);
            versions.Parameters.AddWithValue("report", long.Parse(result.ReportId, CultureInfo.InvariantCulture));
            var history = new List<MrnCorrectionMetadata>();
            await using (var reader = await versions.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.IsDBNull(0)) throw new InvalidDataException();
                    var entry = JsonSerializer.Deserialize<MrnCorrectionMetadata>(reader.GetString(0), MrnCorrectionJson.Options)
                        ?? throw new InvalidDataException();
                    if (entry.Schema != "mrn_correction/v1" || entry.ReportId.ToString(CultureInfo.InvariantCulture) != result.ReportId ||
                        entry.Version <= 0 || entry.Version != reader.GetInt32(1) || entry.ReviewerId != reader.GetInt64(2) ||
                        entry.RequestId == Guid.Empty) throw new InvalidDataException();
                    if (entry.ExceptionId == exceptionId) history.Add(entry);
                }
            if (history.Select(h => h.Version).Distinct().Count() != history.Count)
                throw new InvalidDataException();
            var latest = history.OrderByDescending(h => h.Version).FirstOrDefault();
            result = result with { CurrentCorrectionVersion = latest?.Version ?? 0,
                LastCorrectionRequestId = latest?.RequestId };
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal async Task<MrnEvidence?> ReadInTransactionAsync(long exceptionId, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var reviewer = configuration.GetValue<long>("MrnReview:ReviewerId");
        var allowAll = AllowAllInDevelopment;
        var allowed = configuration.GetSection("MrnReview:AllowedSyntheticCaseIds").Get<long[]>() ?? [];
        if (reviewer <= 0 || !IsCaseAllowed(allowed, allowAll)) return null;
        await using var query = new NpgsqlCommand("""
            SELECT e.report_id, r.storage_uri, r.sha256_hash
            FROM system.exception_queue e JOIN clinical.clinical_report r ON r.report_id=e.report_id
            WHERE e.exception_id=@id AND e.exception_category='MRN_MISMATCH'
              AND NOT r.is_deleted AND (@allowAll OR e.report_id=ANY(@allowed))
              AND EXISTS (SELECT 1 FROM system.user_account WHERE user_id=@reviewer AND is_active)
            """, connection, transaction);
        query.Parameters.AddWithValue("id", exceptionId);
        query.Parameters.AddWithValue("allowAll", allowAll);
        query.Parameters.AddWithValue("allowed", allowed);
        query.Parameters.AddWithValue("reviewer", reviewer);
        long reportId;
        string storage, expectedHash;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            reportId = reader.GetInt64(0);
            storage = reader.GetString(1);
            expectedHash = reader.GetString(2).Trim();
        }
        if (!Regex.IsMatch(storage, @"\APrivateUploads/[a-f0-9]{32}\.(pdf|png|jpg|jpeg)\z"))
            throw new InvalidDataException();
        var root = Path.Combine(environment.ContentRootPath, "PrivateUploads");
        var id = reportId.ToString(CultureInfo.InvariantCulture);
        var directory = Path.Combine(root, "ocr", id);
        var pointer = Path.Combine(directory, "current.txt");
        var attempt = (await File.ReadAllTextAsync(pointer, cancellationToken)).Trim();
        if (!Guid.TryParseExact(attempt, "N", out var attemptId))
            throw new InvalidDataException();

        await using var original = File.OpenRead(Path.Combine(environment.ContentRootPath, storage));
        var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken));
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(directory, attempt, "extracted_fields.json"), cancellationToken));
        var batch = OcrCandidateBatch.Read(json.RootElement, reportId, attempt);
        if (batch.MrnValidation != "MRN_MISMATCH")
            throw new InvalidDataException();
        await using var hashQuery = new NpgsqlCommand("""
            SELECT payload_sha256 FROM system.ocr_candidate_batch
            WHERE report_id=@report AND ocr_attempt_id=@attempt
            """, connection, transaction);
        hashQuery.Parameters.AddWithValue("report", reportId);
        hashQuery.Parameters.AddWithValue("attempt", attemptId);
        if (await hashQuery.ExecuteScalarAsync(cancellationToken) is not string hash ||
            hash.Trim() != batch.PayloadHash)
            throw new InvalidDataException();

        var uploaded = protection.CreateProtector("ImagingAI.Upload.Mrn.v1").Unprotect(
            await File.ReadAllTextAsync(Path.Combine(root, id + ".mrn.protected"), cancellationToken));
        var candidates = new List<MrnEvidenceCandidate>();
        foreach (var candidate in json.RootElement.GetProperty("fields").GetProperty("mrn")
                     .GetProperty("candidates").EnumerateArray())
        {
            var page = candidate.GetProperty("page").GetInt32();
            var bbox = candidate.GetProperty("bbox").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var confidence = candidate.GetProperty("confidence").GetDouble();
            if (page <= 0 || bbox.Length != 4 || !double.IsFinite(confidence) ||
                confidence < 0 || confidence > 1 ||
                candidate.GetProperty("source").GetString() != "PADDLEOCR" ||
                candidate.GetProperty("review_status").GetString() != "UNVERIFIED")
                throw new InvalidDataException();
            candidates.Add(new(candidate.GetProperty("value").GetString()!,
                page, candidate.GetProperty("block_id").GetString()!, bbox, confidence, "UNVERIFIED"));
        }
        if ((await File.ReadAllTextAsync(pointer, cancellationToken)).Trim() != attempt)
            throw new InvalidDataException();
        return new(exceptionId.ToString(CultureInfo.InvariantCulture), id, reviewer, attemptId,
            expectedHash, batch.PayloadHash, uploaded, candidates);
    }
}