using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ImagingAI.Api.Data;
using ImagingAI.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ImagingAI.Api.Services;

public sealed class PdfOcrWorker(
    IServiceScopeFactory scopes,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IDataProtectionProvider dataProtection,
    IExtractedFieldsStore fieldsStore,
    ILogger<PdfOcrWorker> logger) : BackgroundService
{
    private bool mrnResumeSqlFailed;
    private const long WorkerLock = 6152456921939523001L;
    private const string ImageWorkingPdfName = "source_image.pdf";

    private string ResolveUploadPath(string storageUri)
    {
        if (!Regex.IsMatch(storageUri, @"\APrivateUploads/[a-f0-9]{32}\.(pdf|png|jpg|jpeg)\z"))
            throw new InvalidDataException("Unexpected upload storage key");
        return Path.Combine(environment.ContentRootPath, storageUri);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.GetFullPath(configuration["Ocr:ProjectRoot"]
            ?? Path.Combine(environment.ContentRootPath, "..", ".."));
        var python = Path.GetFullPath(
            configuration["Ocr:PythonPath"] ?? Path.Combine(".venv", "Scripts", "python.exe"), root);
        var script = Path.Combine(root, "ocr", "test_pdf.py");
        var deidPython = Path.GetFullPath(configuration["Ocr:DeidPythonPath"]
            ?? Path.Combine(".venv-deid", "Scripts", "python.exe"), root);
        var deidScript = Path.Combine(root, "ocr", "run_image_deid.ps1");
        var powerShell = configuration["Ocr:PowerShellPath"] ?? "powershell.exe";
        var deidEnabled = configuration.GetValue("Ocr:DeidEnabled", true);
        var timeout = TimeSpan.FromSeconds(configuration.GetValue("Ocr:TimeoutSeconds", 1800));
        var uploader = configuration.GetValue<long>("Upload:DevelopmentUploaderId");
        var onlyCase = configuration.GetValue<long?>("Ocr:CaseId");

        if (uploader <= 0 || !File.Exists(python) || !File.Exists(script) || timeout <= TimeSpan.Zero ||
            timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            logger.LogError("OCR worker disabled because its development settings are invalid");
            return;
        }
        if (deidEnabled && (!File.Exists(deidPython) || !File.Exists(deidScript)))
        {
            logger.LogWarning("De-identification worker disabled because its local scripts or environment are missing");
            deidEnabled = false;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await ProcessNextAsync(uploader, python, script, timeout, stoppingToken,
                    deidEnabled, powerShell, deidScript, deidPython, onlyCase))
                    continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (MrnResumeLeaseLostException)
            {
                // A failed session must leave the current queue iteration before handling other work.
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                logger.LogError("OCR worker stopped due to PostgreSQL permissions ({SqlState})", exception.SqlState);
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning("OCR worker could not advance the queue ({ErrorType})", exception.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task<bool> ProcessNextAsync(long uploader, string python, string script,
        TimeSpan timeout, CancellationToken cancellationToken, bool deidEnabled,
        string powerShell, string deidScript, string deidPython, long? onlyCase)
    {
        await using var scope = scopes.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ImagingAiDbContext>();
        await database.Database.OpenConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)database.Database.GetDbConnection();

        // Hold one session lock per database throughout the OCR attempt.
        await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
        acquire.Parameters.AddWithValue("key", WorkerLock);
        if (await acquire.ExecuteScalarAsync(cancellationToken) is not true)
            return false;

        try
        {
            if (deidEnabled && await ProcessMrnResumeNextAsync(scope.ServiceProvider, connection,
                    uploader, onlyCase, powerShell, deidScript, python, deidPython, timeout, cancellationToken))
                return true;

            long caseId;
            string storageUri;
            string status;
            await using (var next = new NpgsqlCommand("""
                SELECT report_id, storage_uri, processing_status FROM clinical.clinical_report
                WHERE uploaded_by = @uploader AND NOT is_deleted
                  AND (@caseId IS NULL OR report_id = @caseId)
                  AND (processing_status IN ('UPLOADED', 'OCR_PROCESSING', 'OCR_COMPLETED')
                    OR (@deid AND processing_status IN ('FIELD_EXTRACTION_COMPLETED', 'DEID_PROCESSING')))
                ORDER BY CASE WHEN processing_status = 'UPLOADED' THEN 1 ELSE 0 END, report_id
                LIMIT 1
                """, connection))
            {
                next.Parameters.AddWithValue("uploader", uploader);
                next.Parameters.AddWithValue("caseId", NpgsqlTypes.NpgsqlDbType.Bigint,
                    (object?)onlyCase ?? DBNull.Value);
                next.Parameters.AddWithValue("deid", deidEnabled);
                await using var row = await next.ExecuteReaderAsync(cancellationToken);
                if (!await row.ReadAsync(cancellationToken))
                    return false;
                caseId = row.GetInt64(0);
                storageUri = row.GetString(1);
                status = row.GetString(2);
            }

            if (status == "OCR_COMPLETED")
            {
                await ExtractFieldsAsync(connection, caseId, python, script, timeout, cancellationToken);
                return true;
            }
            if (status is "FIELD_EXTRACTION_COMPLETED" or "DEID_PROCESSING")
            {
                await DeidentifyAsync(connection, caseId, status, storageUri,
                    powerShell, deidScript, python, deidPython, timeout, cancellationToken);
                return true;
            }

            if (!await SetStatusAsync(connection, caseId, "OCR_PROCESSING", cancellationToken, [status]))
                return false;

            var timer = Stopwatch.StartNew();
            int? exitCode = null;
            var outcome = "OCR_COMPLETED";
            var reason = "success";
            try
            {
                exitCode = await RunOcrAsync(caseId, storageUri, python, script, timeout, cancellationToken);
                if (exitCode != 0)
                {
                    outcome = "FAILED";
                    reason = "python_exit";
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("OCR case {CaseId} interrupted after {ElapsedMs} ms",
                    caseId, timer.ElapsedMilliseconds);
                throw;
            }
            catch (Exception exception)
            {
                outcome = "FAILED";
                reason = exception.GetType().Name;
            }

            await SetStatusAsync(connection, caseId, outcome, cancellationToken, ["OCR_PROCESSING"]);
            logger.LogInformation("OCR case {CaseId}: {Status}, {Reason}, exit {ExitCode}, {ElapsedMs} ms",
                caseId, outcome, reason, exitCode, timer.ElapsedMilliseconds);
            return true;
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                release.Parameters.AddWithValue("key", WorkerLock);
                await release.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    private async Task<bool> ProcessMrnResumeNextAsync(IServiceProvider services, NpgsqlConnection connection,
        long uploader, long? onlyCase, string powerShell, string deidScript, string ocrPython, string deidPython,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (mrnResumeSqlFailed || !environment.IsDevelopment() || !configuration.GetValue("MrnReview:Enabled", false)) return false;
        var credential = configuration["MrnReview:TestCredential"];
        if (string.IsNullOrWhiteSpace(credential) || credential.Length < 32 ||
            !services.GetRequiredService<IMrnCorrectionAccess>().IsAuthorized(credential)) return false;
        var allowed = configuration.GetSection("MrnReview:AllowedSyntheticCaseIds").Get<long[]>() ?? [];
        if (allowed.Length == 0) return false;

        var resume = services.GetRequiredService<MrnResumeService>();
        await resume.RepairPublishedAsync(connection, uploader, onlyCase, cancellationToken,
            async (reportId, runId, token) =>
            {
                try { await CheckResumeLeaseAsync(connection, WorkerLock, runId, token); }
                catch (MrnResumeLeaseLostException error)
                {
                    LogResumeLeaseFailure(reportId, error);
                    throw;
                }
            });

        var candidates = new List<(long ExceptionId, long ReportId)>();
        await using (var next = new NpgsqlCommand("""
            SELECT DISTINCT e.exception_id, r.report_id,
                CASE WHEN r.processing_status='MRN_REVALIDATED' THEN 0 ELSE 1 END AS priority
            FROM system.exception_queue e
            JOIN clinical.clinical_report r ON r.report_id=e.report_id
            JOIN system.review_record v ON v.report_id=r.report_id
                AND v.review_status='MRN_REVALIDATION_PASSED'
                AND (v.review_comment::jsonb ->> 'exceptionId')=e.exception_id::text
            WHERE r.uploaded_by=@uploader AND NOT r.is_deleted
              AND r.processing_status IN ('MRN_REVALIDATED','MRN_DEID_PROCESSING')
              AND r.report_id=ANY(@allowed) AND (@case IS NULL OR r.report_id=@case)
              AND e.exception_category='MRN_MISMATCH' AND e.status='RESOLVED'
              AND (v.review_comment::jsonb -> 'result' ->> 'passed')='true'
              AND (v.review_comment::jsonb -> 'result' ->> 'resumeQueued')='true'
              AND NOT EXISTS (
                  SELECT 1 FROM system.review_record stopped
                  WHERE stopped.report_id=r.report_id AND stopped.review_status='MRN_RESUME_STOPPED'
                    AND (stopped.review_comment::jsonb ->> 'jobId')=
                        (v.review_comment::jsonb -> 'result' ->> 'resumeJobId')
              )
            ORDER BY priority, r.report_id, e.exception_id
            """, connection))
        {
            next.Parameters.AddWithValue("uploader", uploader);
            next.Parameters.AddWithValue("allowed", allowed);
            next.Parameters.AddWithValue("case", NpgsqlTypes.NpgsqlDbType.Bigint,
                (object?)onlyCase ?? DBNull.Value);
            await using var row = await next.ExecuteReaderAsync(cancellationToken);
            while (await row.ReadAsync(cancellationToken))
                candidates.Add((row.GetInt64(0), row.GetInt64(1)));
        }

        foreach (var (exceptionId, reportId) in candidates)
        {
            var lockKey = ResumeLockKey(reportId);
            await using (var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection))
            {
                claim.Parameters.AddWithValue("key", lockKey);
                if (await claim.ExecuteScalarAsync(cancellationToken) is not true) continue;
            }

            try
            {
                if (await resume.ProcessAsync(exceptionId,
                        (work, correction, directory, token) => RunMrnResumeDeidAsync(work, correction, directory,
                            powerShell, deidScript, ocrPython, deidPython, timeout, token),
                        cancellationToken, (runId, token) => CheckResumeLeaseAsync(connection, lockKey, runId, token)))
                    return true;
            }
            catch (MrnResumeLeaseLostException error)
            {
                LogResumeLeaseFailure(reportId, error);
                throw;
            }
            finally
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
                    release.Parameters.AddWithValue("key", lockKey);
                    await release.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
        }
        return false;
    }

    private void LogResumeLeaseFailure(long reportId, MrnResumeLeaseLostException error)
    {
        logger.LogWarning("MRN resume lease check failed for report {ReportId}, run {RunId}: {ErrorType} ({SqlState})",
            reportId, error.RunId, error.Kind, error.SqlState);
        if (error.Kind == "SqlError") mrnResumeSqlFailed = true;
    }

    private static long ResumeLockKey(long reportId)
    {
        var input = System.Text.Encoding.UTF8.GetBytes($"MRN-RESUME:{reportId.ToString(CultureInfo.InvariantCulture)}");
        return BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(input), 0);
    }

    private static async Task<bool> SetStatusAsync(NpgsqlConnection connection, long caseId,
        string status, CancellationToken cancellationToken, string[] previousStatuses,
        string? exceptionCategory = null, OcrCandidateBatch? batch = null)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var update = new NpgsqlCommand("""
            UPDATE clinical.clinical_report SET processing_status = @status
            WHERE report_id = @id AND NOT is_deleted AND processing_status = ANY(@previous)
            """, connection, transaction);
        update.Parameters.AddWithValue("id", caseId);
        update.Parameters.AddWithValue("status", status);
        update.Parameters.AddWithValue("previous", previousStatuses);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            return false;

        if (batch is not null)
            await batch.StoreAsync(connection, transaction, caseId, cancellationToken);

        await using var log = new NpgsqlCommand("""
            INSERT INTO system.processing_log (report_id, status) VALUES (@id, @status)
            """, connection, transaction);
        log.Parameters.AddWithValue("id", caseId);
        log.Parameters.AddWithValue("status", status);
        await log.ExecuteNonQueryAsync(cancellationToken);
        if (exceptionCategory is not null)
        {
            await using var exception = new NpgsqlCommand("""
                INSERT INTO system.exception_queue (report_id, exception_category, description, status)
                SELECT @id, @category, @description, 'OPEN'
                WHERE NOT EXISTS (
                    SELECT 1 FROM system.exception_queue
                    WHERE report_id = @id AND exception_category = @category
                )
                """, connection, transaction);
            exception.Parameters.AddWithValue("id", caseId);
            exception.Parameters.AddWithValue("category", exceptionCategory);
            exception.Parameters.AddWithValue("description", ExceptionDescription(exceptionCategory));
            await exception.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string ExceptionDescription(string category) => category switch
    {
        "MRN_MISMATCH" => "上傳與 OCR 的病歷號碼不一致，待人工確認。",
        "MRN_MISSING" => "OCR 未辨識到病歷號碼，待人工確認。",
        "MRN_AMBIGUOUS" => "OCR 辨識到多個不同的病歷號碼，待人工確認。",
        "MRN_INVALID_FORMAT" => "OCR 病歷號碼格式異常，待人工確認。",
        "OCR_OUTPUT_INVALID" => "原始 OCR 結果缺失、格式異常或無法擷取。",
        "UPLOAD_MRN_UNAVAILABLE" => "無法讀取上傳的病歷號碼，待人工確認。",
        "DEID_VERIFICATION_FAILED" => "去識別化流程或驗證未通過，待人工確認。",
        "OCR_CANDIDATE_CONFLICT" => "同一次 OCR 的候選版本不一致，待人工確認。",
        "OCR_PATIENT_LINK_CONFLICT" => "病例已關聯其他病人，保留原關聯。",
        "OCR_PATIENT_WRITE_FAILED" => "測試病人資料寫入或病例關聯未完成。",
        _ => "無法保存 OCR 欄位擷取結果，待人工確認。"
    };

    private string CaseDirectory(long caseId) => Path.Combine(environment.ContentRootPath,
        "PrivateUploads", "ocr", caseId.ToString(CultureInfo.InvariantCulture));

    private async Task ExtractFieldsAsync(NpgsqlConnection connection, long caseId, string python,
        string ocrScript, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var caseDirectory = CaseDirectory(caseId);
        string category = "OCR_OUTPUT_INVALID";
        string status = "FIELD_EXTRACTION_FAILED";
        string? temporary = null;
        OcrCandidateBatch? batch = null;
        try
        {
            var attemptId = (await File.ReadAllTextAsync(
                Path.Combine(caseDirectory, "current.txt"), cancellationToken)).Trim();
            if (!Guid.TryParseExact(attemptId, "N", out _))
                throw new InvalidDataException();
            var attempt = Path.Combine(caseDirectory, attemptId);
            var existingFields = Path.Combine(attempt, "extracted_fields.json");
            if (File.Exists(existingFields))
            {
                using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(existingFields, cancellationToken));
                batch = OcrCandidateBatch.Read(saved.RootElement, caseId, attemptId);
            }
            else
            {
                category = "FIELD_STORAGE_FAILED";
                PrivateJsonFieldsStore.RestrictAttemptDirectory(attempt);
                category = "OCR_OUTPUT_INVALID";
                temporary = Path.Combine(attempt, $".extract-{Guid.NewGuid():N}.tmp");
                var script = Path.Combine(Path.GetDirectoryName(ocrScript)!, "extract_report_fields.py");
                var exit = await RunPythonAsync(python, script,
                    ["--input", Path.Combine(attempt, "ocr_result.json"), "--output", temporary],
                    timeout, cancellationToken);
                if (exit != 0)
                    throw new InvalidDataException();

                var result = JsonNode.Parse(await File.ReadAllTextAsync(temporary, cancellationToken))!.AsObject();
                var values = result["fields"]!["mrn"]!["candidates"]!.AsArray()
                    .Select(candidate => candidate!["value"]!.GetValue<string>().Trim())
                    .Where(value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                category = "UPLOAD_MRN_UNAVAILABLE";
                var mrnPath = Path.Combine(environment.ContentRootPath, "PrivateUploads",
                    caseId.ToString(CultureInfo.InvariantCulture) + ".mrn.protected");
                var uploaded = dataProtection.CreateProtector("ImagingAI.Upload.Mrn.v1")
                    .Unprotect(await File.ReadAllTextAsync(mrnPath, cancellationToken)).Trim();
                var match = values.Length switch
                {
                    0 => "MRN_MISSING",
                    > 1 => "MRN_AMBIGUOUS",
                    _ when !MrnFormat.IsValid(values[0]) => "MRN_INVALID_FORMAT",
                    _ when !string.Equals(values[0], uploaded, StringComparison.Ordinal) => "MRN_MISMATCH",
                    _ => "MATCH"
                };

                result["case_id"] = caseId.ToString(CultureInfo.InvariantCulture);
                result["ocr_attempt_id"] = attemptId;
                result["mrn_validation"] = match;
                using var prepared = JsonDocument.Parse(result.ToJsonString());
                var extracted = OcrCandidateBatch.Read(prepared.RootElement, caseId, attemptId);
                category = "FIELD_STORAGE_FAILED";
                await fieldsStore.WriteAsync(attempt, result, cancellationToken);
                batch = extracted;
            }
            category = batch.MrnValidation;
            status = "FIELD_EXTRACTION_COMPLETED";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
            JsonException or InvalidOperationException or KeyNotFoundException or TimeoutException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Field extraction case {CaseId}: {Category} ({ErrorType})",
                caseId, category, exception.GetType().Name);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Field extraction temporary cleanup failed for case {CaseId}", caseId);
                }
            }
        }

        if (!await CommitCandidateStageAsync(connection, caseId, status, "OCR_COMPLETED",
                category == "MATCH" ? null : category, batch, cancellationToken))
            return;
        logger.LogInformation("Field extraction case {CaseId}: {Status}, {Category}", caseId, status, category);
    }

    private async Task DeidentifyAsync(NpgsqlConnection connection, long caseId, string previous,
        string storageUri, string powerShell, string script, string ocrPython, string deidPython,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var outcome = "DEID_FAILED";
        var category = "DEID_VERIFICATION_FAILED";
        int? exitCode = null;
        try
        {
            var upload = ResolveUploadPath(storageUri);
            var caseDirectory = CaseDirectory(caseId);
            var ocrId = (await File.ReadAllTextAsync(
                Path.Combine(caseDirectory, "current.txt"), cancellationToken)).Trim();
            if (!Guid.TryParseExact(ocrId, "N", out _))
                throw new InvalidDataException();
            var source = Path.Combine(caseDirectory, ocrId);
            using var fields = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(source, "extracted_fields.json"), cancellationToken));
            var extraction = fields.RootElement;
            var batch = OcrCandidateBatch.Read(extraction, caseId, ocrId);
            if (!await CommitCandidateStageAsync(connection, caseId, "DEID_PROCESSING", previous,
                    null, batch, cancellationToken))
                return;

            var match = extraction.GetProperty("mrn_validation").GetString();
            if (match is "MRN_MISMATCH" or "MRN_MISSING" or "MRN_AMBIGUOUS" or "MRN_INVALID_FORMAT")
            {
                outcome = "MRN_REVIEW_REQUIRED";
                category = match;
            }
            else if (match == "MATCH")
            {
                await TryWriteTestPatientAsync(connection, caseId, extraction, cancellationToken);
                var pdf = Path.GetExtension(upload) == ".pdf"
                    ? upload : Path.Combine(source, ImageWorkingPdfName);
                if (!File.Exists(pdf))
                    throw new FileNotFoundException();
                var ocr = Path.Combine(source, "ocr_result.json");
                using var raw = JsonDocument.Parse(await File.ReadAllTextAsync(ocr, cancellationToken));
                var pageCount = raw.RootElement.GetProperty("total_pages").GetInt32();
                var attempt = Path.Combine(source, "deid", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(attempt);
                PrivateJsonFieldsStore.RestrictAttemptDirectory(attempt);
                exitCode = await RunDeidPipelineAsync(powerShell, script, pdf, ocr,
                    Path.Combine(source, "pages"), attempt, extraction, [], ocrPython, deidPython,
                    timeout, resumeProcessTree: false, cancellationToken);

                if (exitCode == 0)
                {
                    await VerifyAndPublishDeidAsync(attempt, source, pageCount, cancellationToken);
                    outcome = "PENDING_REVIEW";
                    category = null;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException or TimeoutException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or FormatException)
        {
            logger.LogWarning("De-identification case {CaseId} failed ({ErrorType})",
                caseId, exception.GetType().Name);
        }

        await SetStatusAsync(connection, caseId, outcome, cancellationToken, [previous, "DEID_PROCESSING"], category);
        logger.LogInformation("De-identification case {CaseId}: {Status}, exit {ExitCode}, {ElapsedMs} ms",
            caseId, outcome, exitCode, timer.ElapsedMilliseconds);
    }

    private async Task TryWriteTestPatientAsync(NpgsqlConnection connection, long caseId,
        JsonElement extraction, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() ||
            !configuration.GetValue("EnableOcrAutoPatientWriteTest", false))
            return;
        if (!configuration.GetValue("OcrAutoPatientWriteBackupConfirmed", false))
        {
            logger.LogWarning("Patient test write skipped for case {CaseId}: backup not confirmed", caseId);
            return;
        }

        string? failure = null;
        try
        {
            var mrnPath = Path.Combine(environment.ContentRootPath, "PrivateUploads",
                caseId.ToString(CultureInfo.InvariantCulture) + ".mrn.protected");
            var uploadedMrn = dataProtection.CreateProtector("ImagingAI.Upload.Mrn.v1")
                .Unprotect(await File.ReadAllTextAsync(mrnPath, cancellationToken));
            var patient = OcrPatientWriter.ReadSyntheticPatient(extraction, uploadedMrn);
            if (patient is not null)
                await OcrPatientWriter.WriteAsync(connection, caseId, patient, cancellationToken);
        }
        catch (Exception exception) when (exception is OcrPatientLinkConflictException or NpgsqlException or
            InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            failure = exception is OcrPatientLinkConflictException
                ? "OCR_PATIENT_LINK_CONFLICT" : "OCR_PATIENT_WRITE_FAILED";
            logger.LogWarning("Patient test write case {CaseId}: {Category} ({SqlState})",
                caseId, failure, (exception as PostgresException)?.SqlState);
        }

        if (failure is null)
            return;

        try
        {
            await SetStatusAsync(connection, caseId, "DEID_PROCESSING", cancellationToken,
                ["DEID_PROCESSING"], failure);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("Patient test exception could not be saved for case {CaseId} ({ErrorType})",
                caseId, exception.GetType().Name);
        }
    }

    private async Task<bool> CommitCandidateStageAsync(NpgsqlConnection connection, long caseId,
        string status, string previous, string? category, OcrCandidateBatch? batch,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SetStatusAsync(connection, caseId, status, cancellationToken, [previous],
                category, batch);
        }
        catch (Exception exception) when (batch is not null &&
            (exception is CandidateBatchConflictException or PostgresException))
        {
            // The candidate transaction has already rolled back. Use a separate transaction
            // to record a visible stop without re-running Presidio or rewriting local evidence.
            var failure = exception is CandidateBatchConflictException
                ? "OCR_CANDIDATE_CONFLICT" : "FIELD_STORAGE_FAILED";
            logger.LogWarning("OCR candidate case {CaseId}: {Category} ({SqlState})",
                caseId, failure, (exception as PostgresException)?.SqlState);
            await SetStatusAsync(connection, caseId, "FIELD_STORAGE_FAILED", cancellationToken, [previous], failure);
            return false;
        }
    }

    private static async Task VerifyAndPublishDeidAsync(string attempt, string source, int pageCount,
        CancellationToken cancellationToken)
    {
        await ValidateDeidResultAsync(attempt, pageCount, cancellationToken);
        var pointer = Path.Combine(attempt, "current.txt");
        await File.WriteAllTextAsync(pointer, Path.GetFileName(attempt), cancellationToken);
        File.Move(pointer, Path.Combine(source, "deid", "current.txt"), overwrite: true);
    }

    internal static async Task ValidateDeidResultAsync(string attempt, int pageCount,
        CancellationToken cancellationToken)
    {
        var resultPath = Path.Combine(attempt, "result_deidentified.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken));
        var result = json.RootElement;
        if (pageCount < 1 ||
            result.GetProperty("schema_version").GetString() != "deid_ocr/v1" ||
            result.GetProperty("page_count").GetInt32() != pageCount ||
            result.GetProperty("pages").GetArrayLength() != pageCount ||
            result.GetProperty("deid_status").GetString() != "AUTO_REDACTED_PENDING_REVIEW" ||
            result.GetProperty("review_status").GetString() != "PENDING_REVIEW" ||
            result.GetProperty("cloud_upload_allowed").GetBoolean() ||
            !result.GetProperty("verification").GetProperty("passed_automated_checks").GetBoolean())
            throw new InvalidDataException();

        var images = Path.Combine(attempt, "result_deidentified_pages");
        for (var page = 1; page <= pageCount; page++)
        {
            if (!File.Exists(Path.Combine(images, $"page_{page:D3}.png")))
                throw new InvalidDataException();
        }
    }

    private async Task RunMrnResumeDeidAsync(MrnResumeWork work, ProtectedMrnCorrection correction,
        string resultDirectory, string powerShell, string script, string ocrPython, string deidPython,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var caseDirectory = CaseDirectory(work.ReportId);
        var currentAttempt = (await File.ReadAllTextAsync(Path.Combine(caseDirectory, "current.txt"), cancellationToken)).Trim();
        if (!Guid.TryParseExact(currentAttempt, "N", out var currentId) || currentId != work.OcrAttemptId)
            throw new InvalidDataException("OCR source attempt changed");
        var source = Path.Combine(caseDirectory, work.OcrAttemptId.ToString("N"));
        var ocr = Path.Combine(source, "ocr_result.json");
        var fieldsPath = Path.Combine(source, "extracted_fields.json");
        using var fields = JsonDocument.Parse(await File.ReadAllBytesAsync(fieldsPath, cancellationToken));
        var extraction = fields.RootElement;
        var batch = OcrCandidateBatch.Read(extraction, work.ReportId, work.OcrAttemptId.ToString("N"));
        if (batch.MrnValidation != "MRN_MISMATCH" ||
            !string.Equals(batch.PayloadHash, work.CandidateSha256, StringComparison.Ordinal))
            throw new InvalidDataException("OCR candidate source changed");
        using var raw = JsonDocument.Parse(await File.ReadAllBytesAsync(ocr, cancellationToken));
        var pageCount = raw.RootElement.GetProperty("total_pages").GetInt32();
        if (pageCount < 1 || raw.RootElement.GetProperty("pages").GetArrayLength() != pageCount)
            throw new InvalidDataException("OCR page source is invalid");

        var upload = ResolveUploadPath(work.StorageUri);
        var pdf = Path.GetExtension(upload) == ".pdf" ? upload : Path.Combine(source, ImageWorkingPdfName);
        if (!File.Exists(pdf)) throw new FileNotFoundException();
        var protectedMrnPath = Path.Combine(environment.ContentRootPath, "PrivateUploads",
            work.ReportId.ToString(CultureInfo.InvariantCulture) + ".mrn.protected");
        var uploadedMrn = dataProtection.CreateProtector("ImagingAI.Upload.Mrn.v1")
            .Unprotect(await File.ReadAllTextAsync(protectedMrnPath, cancellationToken)).Trim();
        var extraMrns = new[] { uploadedMrn, correction.Request.ConfirmedMrn };

        var exit = await RunDeidPipelineAsync(powerShell, script, pdf, ocr, Path.Combine(source, "pages"),
            resultDirectory, extraction, extraMrns, ocrPython, deidPython, timeout,
            resumeProcessTree: true, cancellationToken);
        if (exit != 0) throw new InvalidDataException("De-identification process did not complete successfully");
    }

    public static async Task CheckResumeLeaseAsync(NpgsqlConnection connection, long key, Guid? runId,
        CancellationToken token)
    {
        try
        {
            await using var check = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype='advisory' AND pid=pg_backend_pid() AND granted AND objsubid=1
                      AND ((classid::bigint << 32) | objid::bigint) = @key
                )
                """, connection);
            check.Parameters.AddWithValue("key", key);
            if (await check.ExecuteScalarAsync(token) is not true)
                throw new MrnResumeLeaseLostException("LockMissing", runId);
        }
        catch (PostgresException error)
        {
            var connectionError = error.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                error.SqlState is "57P01" or "57P02" or "57P03";
            throw new MrnResumeLeaseLostException(connectionError ? "ConnectionError" : "SqlError",
                runId, error.SqlState);
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            token.ThrowIfCancellationRequested();
            throw new MrnResumeLeaseLostException("ConnectionError", runId);
        }
    }

    private async Task<int> RunDeidPipelineAsync(string powerShell, string script, string pdf, string ocr,
        string pageImageDirectory, string resultDirectory, JsonElement extraction,
        IReadOnlyCollection<string> additionalMrns, string ocrPython, string deidPython,
        TimeSpan timeout, bool resumeProcessTree, CancellationToken cancellationToken)
    {
        var expected = Path.Combine(resultDirectory, "expected_pii.json");
        var known = BuildKnownPii(extraction, additionalMrns);
        try
        {
            await File.WriteAllTextAsync(expected, JsonSerializer.Serialize(new { known_values = known }), cancellationToken);
            var arguments = new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                "-Pdf", pdf, "-PdfOcrJsonPath", ocr, "-PdfPageImageDir", pageImageDirectory,
                "-ResultDirectory", resultDirectory, "-Name", "result", "-OcrPython", ocrPython,
                "-DeidPython", deidPython, "-ExpectedPii", expected
            };
            return resumeProcessTree
                ? await LocalDeidProcessRunner.RunAsync(powerShell, Path.GetDirectoryName(script)!, arguments,
                    timeout, cancellationToken)
                : await RunProcessAsync(powerShell, Path.GetDirectoryName(script)!, arguments, timeout, cancellationToken);
        }
        finally
        {
            try { File.Delete(expected); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger.LogWarning("De-identification temporary cleanup failed for case {CaseId}", extraction.TryGetProperty("case_id", out var caseId) ? caseId.GetString() : "unknown"); }
        }
    }

    private static List<object> BuildKnownPii(JsonElement extraction, IEnumerable<string> additionalMrns)
    {
        var known = new List<object>();
        var unique = new HashSet<(string Value, string Entity)>(StringTupleComparer.Ordinal);
        foreach (var (field, entity) in new[] { ("mrn", "MRN"), ("patient_name", "PATIENT_NAME") })
        {
            foreach (var candidate in extraction.GetProperty("fields").GetProperty(field)
                         .GetProperty("candidates").EnumerateArray())
            {
                var value = candidate.GetProperty("value").GetString();
                if (!string.IsNullOrWhiteSpace(value) && unique.Add((value, entity)))
                    known.Add(new { value, kind = "full", entity_type = entity });
            }
        }
        foreach (var value in additionalMrns.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            if (unique.Add((value, "MRN"))) known.Add(new { value, kind = "full", entity_type = "MRN" });
        return known;
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Value, string Entity)>
    {
        public static readonly StringTupleComparer Ordinal = new();
        public bool Equals((string Value, string Entity) x, (string Value, string Entity) y) =>
            StringComparer.Ordinal.Equals(x.Value, y.Value) && StringComparer.Ordinal.Equals(x.Entity, y.Entity);
        public int GetHashCode((string Value, string Entity) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Value), StringComparer.Ordinal.GetHashCode(value.Entity));
    }

    private async Task<int> RunOcrAsync(long caseId, string storageUri, string python,
        string script, TimeSpan timeout, CancellationToken stoppingToken)
    {
        var upload = ResolveUploadPath(storageUri);
        if (!File.Exists(upload))
            throw new FileNotFoundException();

        var caseDirectory = CaseDirectory(caseId);
        var attempt = Path.Combine(caseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        PrivateJsonFieldsStore.RestrictAttemptDirectory(attempt);
        var output = Path.Combine(attempt, "ocr_result.json");

        string[] inputArgs = Path.GetExtension(upload) == ".pdf"
            ? ["--pdf", upload]
            : ["--image", upload, "--working-pdf", Path.Combine(attempt, ImageWorkingPdfName)];
        var exit = await RunPythonAsync(python, script,
            [.. inputArgs, "--output", output, "--image-dir", Path.Combine(attempt, "pages")],
            timeout, stoppingToken);
        if (exit != 0)
            return exit;

        await using var result = File.OpenRead(output);
        using var json = await JsonDocument.ParseAsync(result, cancellationToken: stoppingToken);
        var document = json.RootElement;
        if (!document.TryGetProperty("total_pages", out var total) || !total.TryGetInt32(out var count) ||
            count <= 0 || !document.TryGetProperty("pages", out var pages) ||
            pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() != count)
        {
            throw new InvalidDataException("Invalid OCR result");
        }

        var pendingPointer = Path.Combine(attempt, "current.txt");
        await File.WriteAllTextAsync(pendingPointer, Path.GetFileName(attempt), stoppingToken);
        File.Move(pendingPointer, Path.Combine(caseDirectory, "current.txt"), overwrite: true);
        return 0;
    }

    private static async Task<int> RunPythonAsync(string python, string script, string[] arguments,
        TimeSpan timeout, CancellationToken stoppingToken) =>
        await RunProcessAsync(python, Path.GetDirectoryName(script)!, [script, .. arguments], timeout, stoppingToken);

    private static async Task<int> RunProcessAsync(string executable, string workingDirectory, string[] arguments,
        TimeSpan timeout, CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True";

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            stoppingToken.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        finally
        {
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
        }

        return process.ExitCode;
    }
}
