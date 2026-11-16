
using System.Security.Cryptography;
using System.Globalization;
using ImagingAI.Api.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/upload")]
public class UploadController(
    IWebHostEnvironment environment,
    IConfiguration configuration,
    ImagingAiDbContext database,
    IDataProtectionProvider dataProtection,
    ILogger<UploadController> logger) : ControllerBase
{
    private const long MaxFileSize = 20 * 1024 * 1024;

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFileSize + 1024 * 1024)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string medicalRecordNumber,
        CancellationToken cancellationToken,
        [FromForm] string? reportType = null)
    {
        // Until real authentication is available, use an explicitly enabled test account.
        if (!environment.IsDevelopment() ||
            !long.TryParse(configuration["Upload:DevelopmentUploaderId"], out var uploaderId) ||
            uploaderId <= 0)
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(medicalRecordNumber))
        {
            return BadRequest(new
            {
                message = "請輸入病歷號碼 MRN"
            });
        }

        var mrn = medicalRecordNumber.Trim();
        if (mrn.Length > 50 || mrn.Any(char.IsControl))
            return BadRequest(new { message = "病歷號碼長度或格式不符" });

        var requestedType = string.IsNullOrWhiteSpace(reportType) ? "自動辨識" : reportType.Trim();
        if (requestedType.Length > 50 || requestedType.Any(char.IsControl))
            return BadRequest(new { message = "報告類型格式不符" });

        if (file == null || file.Length == 0)
        {
            return BadRequest(new
            {
                message = "請選擇 PDF 檔案"
            });
        }

        if (file.Length > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "檔案不可超過 20 MB"
            });
        }

        if (!string.Equals(
            Path.GetExtension(file.FileName),
            ".pdf",
            StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new
            {
                message = "目前只接受 PDF 檔案"
            });
        }

        // 建立不對外公開的檔案儲存位置
        string uploadFolder = Path.Combine(
            environment.ContentRootPath,
            "PrivateUploads");

        // 使用隨機名稱，避免原始檔名造成路徑問題
        string storedFileName =
            $"{Guid.NewGuid():N}.pdf";

        string filePath = Path.Combine(
            uploadFolder,
            storedFileName);

        string? metadataPath = null;
        bool ownsFile = false;
        bool preserveFiles = false;

        try
        {
            Directory.CreateDirectory(uploadFolder);
            await using (var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write))
            {
                ownsFile = true;
                await file.CopyToAsync(
                    stream,
                    cancellationToken);
            }

            // 檢查 PDF 檔案標頭
            byte[] header = new byte[5];

            await using (var stream = System.IO.File.OpenRead(filePath))
            {
                int read = await stream.ReadAtLeastAsync(
                    header, header.Length, false, cancellationToken);
                if (read != header.Length ||
                    System.Text.Encoding.ASCII.GetString(header)
                        != "%PDF-")
                {
                    return BadRequest(new
                    {
                        message = "檔案不是有效的 PDF 格式"
                    });
                }
            }

            // 計算 SHA-256
            string sha256;

            await using (var stream = System.IO.File.OpenRead(filePath))
            {
                byte[] hash = await SHA256.HashDataAsync(
                    stream,
                    cancellationToken);

                sha256 = Convert.ToHexString(hash);
            }

            var connection = (NpgsqlConnection)database.Database.GetDbConnection();
            await database.Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, cancellationToken);
            var pgTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            NpgsqlCommand Command(string sql) => new(sql, connection, pgTransaction);

            await using (var uploader = Command("""
                SELECT 1 FROM system.user_account WHERE user_id = @id AND is_active
                """))
            {
                uploader.Parameters.AddWithValue("id", uploaderId);
                if (await uploader.ExecuteScalarAsync(cancellationToken) is null)
                    return StatusCode(503, new { message = "測試上傳帳號未啟用" });
            }

            // A transaction-scoped lock serializes submissions for the same PDF.
            await using (var dedupLock = Command("SELECT pg_advisory_xact_lock(hashtextextended(@hash, 0))"))
            {
                dedupLock.Parameters.AddWithValue("hash", sha256);
                await dedupLock.ExecuteNonQueryAsync(cancellationToken);
            }

            var protector = dataProtection.CreateProtector("ImagingAI.Upload.Mrn.v1");
            string MetadataPath(long id) => Path.Combine(
                uploadFolder, id.ToString(CultureInfo.InvariantCulture) + ".mrn.protected");

            long? previousId = null;
            string? previousStatus = null;
            await using (var duplicate = Command("""
                SELECT report_id, processing_status FROM clinical.clinical_report
                WHERE uploaded_by = @actor AND sha256_hash = @hash AND NOT is_deleted
                ORDER BY report_id LIMIT 1
                """))
            {
                duplicate.Parameters.AddWithValue("actor", uploaderId);
                duplicate.Parameters.AddWithValue("hash", sha256);
                await using var reader = await duplicate.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    previousId = reader.GetInt64(0);
                    previousStatus = reader.GetString(1);
                }
            }

            if (previousId is long duplicateId)
            {
                var existingPath = MetadataPath(duplicateId);
                if (!System.IO.File.Exists(existingPath) ||
                    !string.Equals(mrn, protector.Unprotect(
                        await System.IO.File.ReadAllTextAsync(existingPath, cancellationToken)),
                        StringComparison.Ordinal))
                {
                    return Conflict(new { message = "相同檔案的病歷關聯需確認" });
                }

                return Accepted(Result(duplicateId, previousStatus!, true, file.Length));
            }

            // Auto-detection and the existing UI label are provisional text values.
            // Explicit report-type codes must resolve to an active catalog entry.
            long? reportTypeId = null;
            if (requestedType is not ("自動辨識" or "心導管檢查報告"))
            {
                await using var type = Command("""
                    SELECT report_type_id FROM clinical.report_type WHERE type_code = @code AND is_active
                    """);
                type.Parameters.AddWithValue("code", requestedType);
                reportTypeId = (long?)await type.ExecuteScalarAsync(cancellationToken);
                if (reportTypeId is null)
                    return BadRequest(new { message = "報告類型不存在或未啟用" });
            }

            long reportId;
            await using (var insert = Command("""
                INSERT INTO clinical.clinical_report (
                    uploaded_by, report_type, report_type_id, file_name, sha256_hash, storage_uri, processing_status
                ) VALUES (@actor, @type, @typeId, @fileName, @hash, @storage, 'UPLOADED')
                RETURNING report_id
                """))
            {
                insert.Parameters.AddWithValue("actor", uploaderId);
                insert.Parameters.AddWithValue("type", requestedType);
                insert.Parameters.AddWithValue("typeId", NpgsqlTypes.NpgsqlDbType.Bigint,
                    (object?)reportTypeId ?? DBNull.Value);
                insert.Parameters.AddWithValue("fileName", storedFileName);
                insert.Parameters.AddWithValue("hash", sha256);
                insert.Parameters.AddWithValue("storage", $"PrivateUploads/{storedFileName}");
                reportId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
            }

            await using (var log = Command("""
                INSERT INTO system.processing_log (report_id, status)
                VALUES (@id, 'UPLOADED') RETURNING processing_log_id
                """))
            {
                log.Parameters.AddWithValue("id", reportId);
                await log.ExecuteScalarAsync(cancellationToken);
            }

            var protectedMrn = protector.Protect(mrn);
            var path = MetadataPath(reportId);
            await using (var metadata = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                metadataPath = path;
                await metadata.WriteAsync(System.Text.Encoding.UTF8.GetBytes(protectedMrn), cancellationToken);
            }

            // A lost commit acknowledgment can leave the outcome unknown.
            // Keep the files in that case so a committed case can still be processed.
            cancellationToken.ThrowIfCancellationRequested();
            preserveFiles = true;
            await transaction.CommitAsync(CancellationToken.None);
            return Accepted(Result(reportId, "UPLOADED", false, file.Length));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            // PostgreSQL explicitly rejected the operation or commit.
            preserveFiles = false;
            logger.LogWarning("Upload database operation rejected ({SqlState})", exception.SqlState);
            return StatusCode(503, new { message = "無法建立病例紀錄" });
        }
        catch (Exception exception) when (exception is NpgsqlException or IOException or
            UnauthorizedAccessException or CryptographicException or TimeoutException)
        {
            logger.LogWarning("Upload failed ({ErrorType}, preserveFiles: {PreserveFiles})",
                exception.GetType().Name, preserveFiles);
            return StatusCode(503, new { message = "無法儲存病例" });
        }
        finally
        {
            if (!preserveFiles)
            {
                if (metadataPath is not null)
                    DeleteUncommittedFile(metadataPath);
                if (ownsFile)
                    DeleteUncommittedFile(filePath);
            }
        }
    }

    private static object Result(long reportId, string processingStatus, bool duplicate, long fileSize) => new
    {
        status = "Success",
        caseId = reportId.ToString(CultureInfo.InvariantCulture),
        processingStatus,
        duplicate,
        fileSize,
        fileName = $"病例-{reportId}.pdf"
    };

    private void DeleteUncommittedFile(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not clean up an uncommitted upload ({ErrorType})",
                exception.GetType().Name);
        }
    }
}
