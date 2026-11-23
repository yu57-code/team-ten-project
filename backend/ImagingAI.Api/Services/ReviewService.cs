using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using ImagingAI.Api.Models.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ImagingAI.Api.Services;

public sealed class ReviewService(
    ImagingAiDbContext dbContext,
    IWebHostEnvironment environment)
{
    private const string CandidateSchema = "ocr_extracted_fields/v1";
    private const string Unverified = "UNVERIFIED";
    private const string VerifiedDeidStatus = "AUTO_REDACTED_PENDING_REVIEW";
    private const int MaxPreviewBytes = 32 * 1024 * 1024;
    private const int MaxManifestBytes = 32 * 1024 * 1024;
    private const string DraftStatus = "DRAFT";
    private const string DraftSchema = "review_draft/v1";
    private const string ConfirmedStatus = "CONFIRMED";
    private const string ConfirmationSchema = "review_confirmation/v1";
    private const int MaxDraftBytes = 8 * 1024;
    private const int MaxCandidateBatchBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 12
    };

    public async Task<List<PendingReviewDto>> GetPendingAsync(
        CancellationToken cancellationToken)
    {
        return await UnconfirmedReports()
            .Where(r =>
                r.ProcessingStatus == "PENDING_REVIEW" &&
                !r.IsDeleted)
            .OrderByDescending(r => r.UploadedAt)
            .Select(r => new PendingReviewDto(
                r.ReportId,
                r.FileName,
                r.ProcessingStatus,
                r.UploadedAt,
                r.ReportTypeId,
                r.ReportType,
                r.DeidStatus
            ))
            .ToListAsync(cancellationToken);
    }

    public async Task<PendingReviewDto?> GetByIdAsync(
        long reportId,
        CancellationToken cancellationToken)
    {
        return await UnconfirmedReports()
            .Where(r =>
                r.ReportId == reportId &&
                r.ProcessingStatus == "PENDING_REVIEW" &&
                !r.IsDeleted)
            .Select(r => new PendingReviewDto(
                r.ReportId,
                r.FileName,
                r.ProcessingStatus,
                r.UploadedAt,
                r.ReportTypeId,
                r.ReportType,
                r.DeidStatus
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private IQueryable<ClinicalReport> UnconfirmedReports() =>
        dbContext.ClinicalReports.AsNoTracking().Where(report =>
            !dbContext.ReviewRecords.Any(review =>
                review.ReportId == report.ReportId && review.ReviewStatus == ConfirmedStatus));

    private Task<bool> HasConfirmedReviewAsync(long reportId, CancellationToken cancellationToken) =>
        dbContext.ReviewRecords.AnyAsync(review =>
            review.ReportId == reportId && review.ReviewStatus == ConfirmedStatus, cancellationToken);

    internal async Task<ReviewDraftResult> GetDraftAsync(
        long reportId, long reviewerId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await UnconfirmedReports().AnyAsync(report => report.ReportId == reportId &&
                    !report.IsDeleted && report.ProcessingStatus == "PENDING_REVIEW", cancellationToken))
                return new(ReviewDraftStatus.ReportNotFound);

            var rows = await ReadDraftRowsAsync(reportId, reviewerId, cancellationToken);
            return rows.Count switch
            {
                0 => new(ReviewDraftStatus.DraftNotFound),
                1 => DraftSuccess(reportId, reviewerId, rows[0].Id, ReadDraftDocument(rows[0])),
                _ => new(ReviewDraftStatus.Conflict)
            };
        }
        catch (JsonException)
        {
            return new(ReviewDraftStatus.InvalidDraft);
        }
        catch (Exception exception) when (IsDraftDatabaseFailure(exception))
        {
            return new(ReviewDraftStatus.DatabaseFailure);
        }
    }

    internal async Task<ReviewDraftResult> SaveDraftAsync(
        long reportId, long reviewerId, SaveReviewDraftDto request,
        string? ifMatch, string? ifNoneMatch, CancellationToken cancellationToken)
    {
        if (ifMatch is null && ifNoneMatch is null)
            return new(ReviewDraftStatus.PreconditionRequired);
        if ((ifMatch is not null && ifNoneMatch is not null) ||
            (ifNoneMatch is not null && ifNoneMatch != "*"))
            return new(ReviewDraftStatus.PreconditionFailed);
        if (request.OcrAttemptId == Guid.Empty || !ValidDraftFields(request.Fields))
            return new(ReviewDraftStatus.InvalidDraft);

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            var postgresTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            // Serialize creation and updates even when the draft row does not yet exist.
            await using var claim = new NpgsqlCommand("""
                SELECT 1 FROM clinical.clinical_report
                WHERE report_id = @report AND NOT is_deleted AND processing_status = 'PENDING_REVIEW'
                FOR UPDATE
                """, connection, postgresTransaction);
            claim.Parameters.AddWithValue("report", reportId);
            if (await claim.ExecuteScalarAsync(cancellationToken) is null)
                return new(ReviewDraftStatus.ReportNotFound);
            if (await HasConfirmedReviewAsync(reportId, cancellationToken))
                return new(ReviewDraftStatus.Conflict);

            var rows = await ReadDraftRowsAsync(reportId, reviewerId, cancellationToken);
            if (rows.Count > 1)
                return new(ReviewDraftStatus.Conflict);

            var previous = rows.SingleOrDefault();
            if (previous is null ? ifNoneMatch != "*" : ifMatch != DraftETag(previous.Id, previous.Version))
                return new(ReviewDraftStatus.PreconditionFailed);

            var oldDocument = previous is null ? null : ReadDraftDocument(previous);
            var attempt = await ReadDraftAttemptAsync(reportId, cancellationToken);
            if (attempt != request.OcrAttemptId ||
                (oldDocument is not null && oldDocument.OcrAttemptId != attempt) ||
                previous?.Version == int.MaxValue)
                return new(ReviewDraftStatus.Conflict);

            var references = await CheckDraftSourcesAsync(connection, postgresTransaction,
                reportId, request, cancellationToken);
            if (references != ReviewDraftStatus.Success)
                return new(references);

            var now = DateTimeOffset.UtcNow;
            if (oldDocument is not null && oldDocument.UpdatedAt > now)
                now = oldDocument.UpdatedAt;
            var document = new ReviewDraftDocument(DraftSchema, request.OcrAttemptId,
                (previous?.Version ?? 0) + 1, oldDocument?.CreatedAt ?? now, now, request.Fields);
            var json = JsonSerializer.Serialize(document, DraftJson);
            if (Encoding.UTF8.GetByteCount(json) > MaxDraftBytes)
                return new(ReviewDraftStatus.InvalidDraft);

            if (await ReadDraftAttemptAsync(reportId, cancellationToken) != attempt)
                return new(ReviewDraftStatus.Conflict);

            await using var write = new NpgsqlCommand(previous is null
                ? """
                    INSERT INTO system.review_record
                        (report_id, reviewer_id, review_status, reviewed_at, review_comment, data_version)
                    VALUES (@report, @reviewer, 'DRAFT', NULL, @draft, @version)
                    RETURNING review_id
                    """
                : """
                    UPDATE system.review_record SET review_comment = @draft, data_version = @version
                    WHERE review_id = @id AND report_id = @report AND reviewer_id = @reviewer
                        AND review_status = 'DRAFT' AND data_version = @previous_version
                    RETURNING review_id
                    """, connection, postgresTransaction);
            write.Parameters.AddWithValue("report", reportId);
            write.Parameters.AddWithValue("reviewer", reviewerId);
            write.Parameters.AddWithValue("draft", json);
            write.Parameters.AddWithValue("version", document.Version);
            if (previous is not null)
            {
                write.Parameters.AddWithValue("id", previous.Id);
                write.Parameters.AddWithValue("previous_version", previous.Version);
            }
            if (await write.ExecuteScalarAsync(cancellationToken) is not long draftId)
                return new(ReviewDraftStatus.Conflict);

            if (!await WriteDraftAuditAsync(connection, postgresTransaction, reportId, reviewerId,
                    oldDocument, document, cancellationToken))
                return new(ReviewDraftStatus.DatabaseFailure);
            await transaction.CommitAsync(cancellationToken);
            return DraftSuccess(reportId, reviewerId, draftId, document);
        }
        catch (JsonException)
        {
            return new(ReviewDraftStatus.InvalidDraft);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(ReviewDraftStatus.StorageFailure);
        }
        catch (Exception exception) when (IsDraftDatabaseFailure(exception))
        {
            return new(ReviewDraftStatus.DatabaseFailure);
        }
    }

    internal async Task<ReviewConfirmationResult> ConfirmReviewAsync(
        long reportId, long reviewerId, ConfirmReviewRequestDto request,
        string? ifMatch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ifMatch))
            return new(ReviewConfirmationStatus.PreconditionRequired);

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            var postgresTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            await using var claim = new NpgsqlCommand("""
                SELECT processing_status, deid_status FROM clinical.clinical_report
                WHERE report_id = @report AND NOT is_deleted
                FOR UPDATE
                """, connection, postgresTransaction);
            claim.Parameters.AddWithValue("report", reportId);
            await using (var report = await claim.ExecuteReaderAsync(cancellationToken))
            {
                if (!await report.ReadAsync(cancellationToken))
                    return new(ReviewConfirmationStatus.ReportNotFound);
                if (report.GetString(0) != "PENDING_REVIEW" || report.GetString(1) != VerifiedDeidStatus)
                    return new(ReviewConfirmationStatus.ReportNotEligible);
            }

            if (await HasConfirmedReviewAsync(reportId, cancellationToken))
                return new(ReviewConfirmationStatus.AlreadyConfirmed);

            var rows = await ReadDraftRowsAsync(reportId, reviewerId, cancellationToken);
            if (rows.Count == 0)
                return new(ReviewConfirmationStatus.DraftNotFound);
            if (rows.Count > 1)
                return new(ReviewConfirmationStatus.DuplicateDraft);

            var row = rows[0];
            if (request.DraftVersion != row.Version || ifMatch != DraftETag(row.Id, row.Version))
                return new(ReviewConfirmationStatus.PreconditionFailed);

            var draft = ReadDraftDocument(row);
            var attempt = await ReadDraftAttemptAsync(reportId, cancellationToken);
            if (attempt != draft.OcrAttemptId || attempt != request.OcrAttemptId)
                return new(ReviewConfirmationStatus.OcrAttemptConflict);

            var sources = await CheckDraftSourcesAsync(connection, postgresTransaction, reportId,
                new SaveReviewDraftDto(draft.OcrAttemptId, draft.Fields), cancellationToken);
            if (sources != ReviewDraftStatus.Success)
                return new(ReviewConfirmationStatus.InvalidSourceReference);

            // No persisted exception status has been approved as terminal for confirmation.
            if (await dbContext.ExceptionQueues.AnyAsync(item => item.ReportId == reportId, cancellationToken))
                return new(ReviewConfirmationStatus.BlockingException);

            var policy = CheckConfirmationPolicy();
            if (policy != ReviewConfirmationStatus.Success)
                return new(policy);

            if (await ReadDraftAttemptAsync(reportId, cancellationToken) != attempt)
                return new(ReviewConfirmationStatus.OcrAttemptConflict);

            var snapshot = new ReviewConfirmationSnapshot(ConfirmationSchema, reportId, draft.OcrAttemptId,
                row.Id, row.Version, DateTimeOffset.UtcNow, draft.Fields);
            var confirmationId = await WriteConfirmationAsync(
                connection, postgresTransaction, reviewerId, snapshot, cancellationToken);
            if (confirmationId is null)
                return new(ReviewConfirmationStatus.DatabaseFailure);

            await transaction.CommitAsync(cancellationToken);
            return new(ReviewConfirmationStatus.Success, confirmationId);
        }
        catch (JsonException)
        {
            return new(ReviewConfirmationStatus.InvalidDraft);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(ReviewConfirmationStatus.StorageFailure);
        }
        catch (Exception exception) when (IsDraftDatabaseFailure(exception))
        {
            return new(ReviewConfirmationStatus.DatabaseFailure);
        }
    }

    // Required fields, MRN and human-completion rules still need an agreed contract.
    private static ReviewConfirmationStatus CheckConfirmationPolicy() =>
        ReviewConfirmationStatus.ConfirmationPolicyNotReady;

    private static async Task<long?> WriteConfirmationAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long reviewerId,
        ReviewConfirmationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var timestamp = DateTime.SpecifyKind(snapshot.ConfirmedAt.UtcDateTime, DateTimeKind.Unspecified);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO system.review_record
                (report_id, reviewer_id, review_status, reviewed_at, review_comment, data_version)
            VALUES (@report, @reviewer, @status, @at, @snapshot, @version)
            RETURNING review_id
            """, connection, transaction);
        insert.Parameters.AddWithValue("report", snapshot.ReportId);
        insert.Parameters.AddWithValue("reviewer", reviewerId);
        insert.Parameters.AddWithValue("status", ConfirmedStatus);
        insert.Parameters.AddWithValue("at", NpgsqlTypes.NpgsqlDbType.Timestamp, timestamp);
        insert.Parameters.AddWithValue("snapshot", JsonSerializer.Serialize(snapshot, DraftJson));
        insert.Parameters.AddWithValue("version", snapshot.SourceDraftVersion);
        if (await insert.ExecuteScalarAsync(cancellationToken) is not long confirmationId)
            return null;

        await using var audit = new NpgsqlCommand("""
            INSERT INTO system.audit_trail
                (report_id, actor_id, action, action_at, before_json, after_json, data_version)
            VALUES (@report, @actor, 'REVIEW_CONFIRM', @at, NULL, @metadata, @version)
            """, connection, transaction);
        audit.Parameters.AddWithValue("report", snapshot.ReportId);
        audit.Parameters.AddWithValue("actor", reviewerId);
        audit.Parameters.AddWithValue("at", NpgsqlTypes.NpgsqlDbType.Timestamp, timestamp);
        audit.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(new
        {
            confirmationId,
            snapshot.SourceDraftReviewId,
            snapshot.SourceDraftVersion,
            snapshot.ConfirmedAt
        }, DraftJson));
        audit.Parameters.AddWithValue("version", snapshot.SourceDraftVersion);
        return await audit.ExecuteNonQueryAsync(cancellationToken) == 1 ? confirmationId : null;
    }

    private sealed record DraftRow(long Id, int Version, string? Json, DateTime? ReviewedAt);

    private Task<List<DraftRow>> ReadDraftRowsAsync(
        long reportId, long reviewerId, CancellationToken cancellationToken) =>
        dbContext.ReviewRecords.AsNoTracking()
            .Where(row => row.ReportId == reportId && row.ReviewerId == reviewerId && row.ReviewStatus == DraftStatus)
            .Select(row => new DraftRow(row.ReviewId, row.DataVersion, row.ReviewComment, row.ReviewedAt))
            .Take(2).ToListAsync(cancellationToken);

    private static ReviewDraftDocument ReadDraftDocument(DraftRow row)
    {
        if (row.ReviewedAt is not null || row.Json is null || Encoding.UTF8.GetByteCount(row.Json) > MaxDraftBytes)
            throw new JsonException("Invalid review draft.");
        var document = JsonSerializer.Deserialize<ReviewDraftDocument>(row.Json, DraftJson);
        if (document is null || document.SchemaVersion != DraftSchema || document.Version < 1 ||
            document.Version != row.Version || document.OcrAttemptId == Guid.Empty ||
            document.CreatedAt == default || document.UpdatedAt < document.CreatedAt ||
            document.CreatedAt.Offset != TimeSpan.Zero || document.UpdatedAt.Offset != TimeSpan.Zero ||
            !ValidDraftFields(document.Fields))
            throw new JsonException("Invalid review draft.");

        return document;
    }

    private static bool ValidDraftFields(Dictionary<string, ReviewDraftFieldDto>? fields) =>
        fields is not null && fields.Count <= 5 && fields.All(entry =>
            CandidateFieldName(entry.Key) is not null && entry.Value is not null &&
            (entry.Value.Value is null || (entry.Value.Value.Length <= 256 && !entry.Value.Value.Any(char.IsControl))) &&
            (entry.Value.SourceReference is not { } source ||
             (source.FieldName == entry.Key && source.Page > 0 && source.BlockId is { Length: > 0 and <= 128 }
              && !string.IsNullOrWhiteSpace(source.BlockId) && !source.BlockId.Any(char.IsControl))));

    private static string? CandidateFieldName(string draftField) => draftField switch
    {
        "patientName" => "patient_name",
        "mrn" => "mrn",
        "gender" => "gender",
        "reportDate" => "report_date",
        "reportType" => "report_type",
        _ => null
    };

    private async Task<Guid?> ReadDraftAttemptAsync(long reportId, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(environment.ContentRootPath);
        var directory = Path.Combine(root, "PrivateUploads", "ocr", reportId.ToString(CultureInfo.InvariantCulture));
        try
        {
            return await ReadPreviewAttemptAsync(root, directory, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static async Task<ReviewDraftStatus> CheckDraftSourcesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long reportId,
        SaveReviewDraftDto request, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT CASE WHEN octet_length(candidates_json::text) <= @limit THEN candidates_json::text END
            FROM system.ocr_candidate_batch
            WHERE report_id = @report AND ocr_attempt_id = @attempt AND schema_version = @schema
                AND review_status = 'UNVERIFIED' AND mrn_validation = 'MATCH'
            """, connection, transaction);
        command.Parameters.AddWithValue("limit", MaxCandidateBatchBytes);
        command.Parameters.AddWithValue("report", reportId);
        command.Parameters.AddWithValue("attempt", request.OcrAttemptId);
        command.Parameters.AddWithValue("schema", CandidateSchema);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string json)
            return ReviewDraftStatus.Conflict;

        try
        {
            using var payload = JsonDocument.Parse(json);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
                return ReviewDraftStatus.Conflict;
            foreach (var (field, correction) in request.Fields)
            {
                if (correction.SourceReference is not { } source)
                    continue;
                var candidates = ParseField(payload.RootElement, CandidateFieldName(field)!).Candidates;
                if (!candidates.Any(candidate => candidate.Page == source.Page && candidate.BlockId == source.BlockId))
                    return ReviewDraftStatus.InvalidSourceReference;
            }
        }
        catch (JsonException)
        {
            return ReviewDraftStatus.Conflict;
        }

        return ReviewDraftStatus.Success;
    }

    private static async Task<bool> WriteDraftAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long reportId, long reviewerId,
        ReviewDraftDocument? previous, ReviewDraftDocument document, CancellationToken cancellationToken)
    {
        var oldFields = previous?.Fields ?? [];
        var changedFields = oldFields.Keys.Union(document.Fields.Keys, StringComparer.Ordinal)
            .Where(field => oldFields.GetValueOrDefault(field) != document.Fields.GetValueOrDefault(field))
            .Order(StringComparer.Ordinal).ToArray();
        await using var audit = new NpgsqlCommand("""
            INSERT INTO system.audit_trail
                (report_id, actor_id, action, action_at, before_json, after_json, data_version)
            VALUES (@report, @actor, @action, @at, @before, @after, @version)
            """, connection, transaction);
        audit.Parameters.AddWithValue("report", reportId);
        audit.Parameters.AddWithValue("actor", reviewerId);
        audit.Parameters.AddWithValue("action", previous is null ? "REVIEW_DRAFT_CREATE" : "REVIEW_DRAFT_UPDATE");
        audit.Parameters.AddWithValue("at", NpgsqlTypes.NpgsqlDbType.Timestamp,
            DateTime.SpecifyKind(document.UpdatedAt.UtcDateTime, DateTimeKind.Unspecified));
        audit.Parameters.AddWithValue("before", JsonSerializer.Serialize(new { version = previous?.Version }, DraftJson));
        audit.Parameters.AddWithValue("after", JsonSerializer.Serialize(new { version = document.Version, changedFields }, DraftJson));
        audit.Parameters.AddWithValue("version", document.Version);
        return await audit.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static bool IsDraftDatabaseFailure(Exception exception) =>
        exception is DbException or TimeoutException or
            InvalidOperationException { InnerException: DbException or TimeoutException };

    private static string DraftETag(long id, int version) => FormattableString.Invariant($"\"{id}:{version}\"");

    private static ReviewDraftResult DraftSuccess(
        long reportId, long reviewerId, long draftId, ReviewDraftDocument draft) =>
        new(ReviewDraftStatus.Success, new ReviewDraftDto(reportId, reviewerId, DraftStatus,
            draft.OcrAttemptId, draft.Version, draft.CreatedAt, draft.UpdatedAt, draft.Fields),
            DraftETag(draftId, draft.Version));

    internal async Task<ReviewPreviewResult> GetPreviewAsync(
        long reportId, int page, CancellationToken cancellationToken)
    {
        if (page < 1)
            return new(ReviewPreviewStatus.InvalidPage);

        try
        {
            var report = await UnconfirmedReports()
                .Where(report => report.ReportId == reportId && !report.IsDeleted)
                .Select(report => new { report.ProcessingStatus, report.DeidStatus })
                .SingleOrDefaultAsync(cancellationToken);

            if (report is null)
                return new(ReviewPreviewStatus.ReportNotFound);
            if (report.ProcessingStatus != "PENDING_REVIEW" || report.DeidStatus != VerifiedDeidStatus)
                return new(ReviewPreviewStatus.ReportNotEligible);
        }
        catch (Exception exception) when (exception is DbException or TimeoutException or
            InvalidOperationException { InnerException: DbException or TimeoutException })
        {
            return new(ReviewPreviewStatus.DatabaseFailure);
        }

        try
        {
            var root = Path.GetFullPath(environment.ContentRootPath);
            var reportDirectory = Path.Combine(root, "PrivateUploads", "ocr",
                reportId.ToString(CultureInfo.InvariantCulture));
            var ocrAttempt = await ReadPreviewAttemptAsync(root, reportDirectory, cancellationToken);
            if (ocrAttempt is null)
                return new(ReviewPreviewStatus.CurrentOcrAttemptMissing);

            var deidDirectory = Path.Combine(reportDirectory, ocrAttempt.Value.ToString("N"), "deid");
            var deidAttempt = await ReadPreviewAttemptAsync(root, deidDirectory, cancellationToken);
            if (deidAttempt is null)
                return new(ReviewPreviewStatus.CurrentDeidAttemptMissing);

            var published = Path.Combine(deidDirectory, deidAttempt.Value.ToString("N"));
            var manifestBytes = await ReadPreviewFileAsync(root,
                Path.Combine(published, "result_deidentified.json"), MaxManifestBytes, cancellationToken);
            using var manifest = JsonDocument.Parse(manifestBytes);
            var filename = $"page_{page.ToString("D3", CultureInfo.InvariantCulture)}.png";
            var dimensions = ValidatePreviewManifest(manifest.RootElement, page, filename);
            if (dimensions is null)
                return new(ReviewPreviewStatus.InvalidPage);

            try
            {
                var png = await ReadPreviewFileAsync(root,
                    Path.Combine(published, "result_deidentified_pages", filename),
                    MaxPreviewBytes, cancellationToken);
                return IsPreviewPng(png, dimensions.Value.Width, dimensions.Value.Height)
                    ? new(ReviewPreviewStatus.Success, png)
                    : new(ReviewPreviewStatus.PreviewImageInvalid);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(ReviewPreviewStatus.PreviewImageMissing);
            }
            catch (InvalidDataException)
            {
                return new(ReviewPreviewStatus.PreviewImageInvalid);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or
            FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ReviewPreviewStatus.InvalidPublishedArtifact);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(ReviewPreviewStatus.StorageFailure);
        }
    }

    private static async Task<Guid?> ReadPreviewAttemptAsync(
        string root, string directory, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await ReadPreviewFileAsync(root, Path.Combine(directory, "current.txt"),
                128, cancellationToken);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Trim();
        if (!Guid.TryParseExact(text, "N", out var attempt) || attempt == Guid.Empty)
            throw new InvalidDataException();

        return attempt;
    }

    private static async Task<byte[]> ReadPreviewFileAsync(
        string root, string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(root, fullPath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath) || parts.Any(part => part is "." or ".." or ""))
            throw new InvalidDataException();

        CheckPreviewPath(root, parts);
        using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Recheck while the handle is held. The directory ACL must prevent untrusted replacement.
        CheckPreviewPath(root, parts);

        var length = file.Length;
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException();

        var bytes = new byte[(int)length];
        await file.ReadExactlyAsync(bytes, cancellationToken);
        if (file.ReadByte() != -1)
            throw new InvalidDataException();

        return bytes;
    }

    private static void CheckPreviewPath(string root, string[] parts)
    {
        var path = root;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException();

        for (var index = 0; index < parts.Length; index++)
        {
            path = Path.Combine(path, parts[index]);
            attributes = File.GetAttributes(path);
            var expectedDirectory = index < parts.Length - 1;
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                ((attributes & FileAttributes.Directory) != 0) != expectedDirectory)
                throw new InvalidDataException();
        }
    }

    private static (int Width, int Height)? ValidatePreviewManifest(
        JsonElement document, int page, string filename)
    {
        if (!HasPreviewString(document, "schema_version", "deid_ocr/v1") ||
            !HasPreviewString(document, "deid_status", VerifiedDeidStatus) ||
            !HasPreviewString(document, "review_status", "PENDING_REVIEW") ||
            !document.TryGetProperty("cloud_upload_allowed", out var cloud) || cloud.ValueKind != JsonValueKind.False ||
            !PassedPreviewChecks(document) ||
            !TryGetPreviewInteger(document, "page_count", out var count) || count < 1 ||
            !document.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array ||
            pages.GetArrayLength() != count)
            throw new InvalidDataException();

        if (page > count)
            return null;

        var metadata = pages[page - 1];
        if (!HasPreviewString(metadata, "image_file", filename) ||
            !TryGetPreviewInteger(metadata, "page", out var declaredPage) || declaredPage != page ||
            !PassedPreviewChecks(metadata) ||
            !metadata.TryGetProperty("image_size", out var size) ||
            !TryGetPreviewInteger(size, "width", out var width) || width is < 1 or > 16384 ||
            !TryGetPreviewInteger(size, "height", out var height) || height is < 1 or > 16384 ||
            (long)width * height > 100_000_000)
            throw new InvalidDataException();

        return (width, height);
    }

    private static bool HasPreviewString(JsonElement item, string name, string expected) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private static bool PassedPreviewChecks(JsonElement item) =>
        item.TryGetProperty("verification", out var verification) && verification.ValueKind == JsonValueKind.Object &&
        verification.TryGetProperty("passed_automated_checks", out var passed) && passed.ValueKind == JsonValueKind.True;

    private static bool TryGetPreviewInteger(JsonElement item, string name, out int number)
    {
        number = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    private static bool IsPreviewPng(ReadOnlySpan<byte> bytes, int width, int height)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!bytes.StartsWith(signature))
            return false;

        var offset = signature.Length;
        var hasImageData = false;
        while (bytes.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
            if (length > bytes.Length - offset - 12)
                return false;

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, (int)length);
            if (offset == signature.Length)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13 ||
                    BinaryPrimitives.ReadUInt32BigEndian(data) != width ||
                    BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != height ||
                    !IsPreviewPngColor(data[8], data[9]) ||
                    data[10] != 0 || data[11] != 0 || data[12] > 1)
                    return false;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return false;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                hasImageData |= length > 0;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return hasImageData && length == 0 && offset + 12 == bytes.Length;
            }

            offset += 12 + (int)length;
        }

        return false;
    }

    private static bool IsPreviewPngColor(byte bitDepth, byte colorType) => colorType switch
    {
        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
        2 or 4 or 6 => bitDepth is 8 or 16,
        3 => bitDepth is 1 or 2 or 4 or 8,
        _ => false
    };

    internal async Task<ReviewFieldsResult> GetFieldsAsync(
        long reportId, CancellationToken cancellationToken)
    {
        try
        {
            if (!await UnconfirmedReports().AnyAsync(
                    report => report.ReportId == reportId && !report.IsDeleted
                        && report.ProcessingStatus == "PENDING_REVIEW", cancellationToken))
                return ReviewFieldsResult.Failed(ReviewFieldsError.ReportNotFound);

            var (attemptId, pointerError) = await ReadCurrentAttemptAsync(
                reportId, cancellationToken);
            if (pointerError is { } error)
                return ReviewFieldsResult.Failed(error);

            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                // Recheck eligibility in the same statement that selects the batch.
                await using var command = new NpgsqlCommand("""
                    SELECT batch.ocr_attempt_id, batch.created_at, batch.schema_version,
                           batch.review_status, batch.mrn_validation, batch.candidates_json
                    FROM clinical.clinical_report AS report
                    LEFT JOIN system.ocr_candidate_batch AS batch
                      ON batch.report_id = report.report_id AND batch.ocr_attempt_id = @attempt
                    WHERE report.report_id = @report AND NOT report.is_deleted
                      AND report.processing_status = 'PENDING_REVIEW'
                      AND NOT EXISTS (
                        SELECT 1 FROM system.review_record AS review
                        WHERE review.report_id = report.report_id AND review.review_status = @confirmed)
                    """, (NpgsqlConnection)dbContext.Database.GetDbConnection());
                command.Parameters.AddWithValue("report", reportId);
                command.Parameters.AddWithValue("attempt", attemptId);
                command.Parameters.AddWithValue("confirmed", ConfirmedStatus);
                await using var row = await command.ExecuteReaderAsync(cancellationToken);
                if (!await row.ReadAsync(cancellationToken))
                    return ReviewFieldsResult.Failed(ReviewFieldsError.ReportNotFound);
                if (row.IsDBNull(0))
                    return ReviewFieldsResult.Failed(ReviewFieldsError.CandidateBatchNotFound);

                var schema = row.GetString(2);
                if (schema != CandidateSchema)
                    return ReviewFieldsResult.Failed(ReviewFieldsError.UnsupportedSchema);

                var reviewStatus = row.GetString(3);
                var mrnValidation = row.GetString(4);
                if (reviewStatus != Unverified || mrnValidation != "MATCH")
                    return ReviewFieldsResult.Failed(ReviewFieldsError.InvalidCandidatePayload);

                var fields = ParseCandidateFields(row.GetString(5));
                return new ReviewFieldsResult(
                    new ReviewFieldsDto(reportId, attemptId, row.GetFieldValue<DateTimeOffset>(1),
                        schema, reviewStatus, mrnValidation, fields), null);
            }
            finally
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
        catch (JsonException)
        {
            return ReviewFieldsResult.Failed(ReviewFieldsError.InvalidCandidatePayload);
        }
        catch (Exception exception) when (exception is DbException or TimeoutException or
            InvalidOperationException { InnerException: DbException or TimeoutException })
        {
            return ReviewFieldsResult.Failed(ReviewFieldsError.DatabaseFailure);
        }
    }

    private async Task<(Guid AttemptId, ReviewFieldsError? Error)> ReadCurrentAttemptAsync(
        long reportId, CancellationToken cancellationToken)
    {
        // Use the worker's private storage root and a numeric directory name.
        var path = environment.ContentRootPath;
        try
        {
            foreach (var part in new[] { "PrivateUploads", "ocr",
                         reportId.ToString(CultureInfo.InvariantCulture), "current.txt" })
            {
                path = Path.Combine(path, part);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return (default, ReviewFieldsError.CurrentAttemptInvalid);
            }

            var pointer = await File.ReadAllTextAsync(path, cancellationToken);
            return Guid.TryParseExact(pointer.Trim(), "N", out var attemptId) && attemptId != Guid.Empty
                ? (attemptId, null)
                : (default, ReviewFieldsError.CurrentAttemptInvalid);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (default, ReviewFieldsError.CurrentAttemptMissing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (default, ReviewFieldsError.CurrentAttemptInvalid);
        }
    }

    private static ReviewCandidateFieldsDto ParseCandidateFields(string json)
    {
        using var payload = JsonDocument.Parse(json);
        var fields = payload.RootElement;
        if (fields.ValueKind != JsonValueKind.Object)
            throw new JsonException("Invalid OCR candidate fields.");

        return new ReviewCandidateFieldsDto(
            ParseField(fields, "patient_name"), ParseField(fields, "mrn"),
            ParseField(fields, "gender"), ParseField(fields, "report_date"),
            ParseField(fields, "report_type"));
    }

    private static ReviewFieldDto ParseField(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var field))
            return new ReviewFieldDto("missing", null, []);

        if (field.ValueKind != JsonValueKind.Object ||
            !field.TryGetProperty("value", out var value) ||
            value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
            !field.TryGetProperty("candidates", out var array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid OCR candidate field.");

        var candidates = array.EnumerateArray().Select(ParseCandidate).ToArray();
        var values = candidates.Select(candidate => candidate.Value).Distinct(StringComparer.Ordinal).ToArray();
        var singleValue = values.Length == 1 ? values[0] : null;
        if (!string.Equals(value.GetString(), singleValue, StringComparison.Ordinal))
            throw new JsonException("Inconsistent OCR candidate field.");

        var status = values.Length switch
        {
            0 => "missing",
            1 => "available",
            _ => "ambiguous"
        };
        return new ReviewFieldDto(status, singleValue, candidates);
    }

    private static ReviewFieldCandidateDto ParseCandidate(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Number ||
            !page.TryGetInt32(out var pageNumber) || pageNumber < 1 ||
            !item.TryGetProperty("bbox", out var bbox) || bbox.ValueKind != JsonValueKind.Array ||
            bbox.GetArrayLength() != 4 ||
            !item.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number ||
            !confidence.TryGetDouble(out var score) || !double.IsFinite(score) || score is < 0 or > 1 ||
            ReadCandidateString(item, "source") != "PADDLEOCR" ||
            ReadCandidateString(item, "review_status") != Unverified)
            throw new JsonException("Invalid OCR candidate evidence.");

        var coordinates = new int[4];
        for (var index = 0; index < coordinates.Length; index++)
        {
            if (bbox[index].ValueKind != JsonValueKind.Number ||
                !bbox[index].TryGetInt32(out coordinates[index]) || coordinates[index] < 0)
                throw new JsonException("Invalid OCR candidate bounds.");
        }
        if (coordinates[0] >= coordinates[2] || coordinates[1] >= coordinates[3])
            throw new JsonException("Invalid OCR candidate bounds.");

        return new ReviewFieldCandidateDto(ReadCandidateString(item, "value"), "PADDLEOCR",
            pageNumber, ReadCandidateString(item, "block_id"), coordinates, score, Unverified);
    }

    private static string ReadCandidateString(JsonElement item, string name)
    {
        if (item.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { } value && !string.IsNullOrWhiteSpace(value))
            return value;

        throw new JsonException("Invalid OCR candidate text field.");
    }
}
