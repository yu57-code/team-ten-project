
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/upload")]
public class UploadController : ControllerBase
{
    private const long MaxFileSize = 20 * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg"
        };

    private readonly IWebHostEnvironment _environment;

    public UploadController(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFileSize + 1024 * 1024)]
    public async Task<IActionResult> Upload(
        IFormFile file,
        [FromForm] string medicalRecordNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(medicalRecordNumber))
        {
            return BadRequest(new
            {
                message = "請輸入病歷號碼 MRN"
            });
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest(new
            {
                message = "請選擇 PDF、PNG、JPG 或 JPEG 檔案"
            });
        }

        if (file.Length > MaxFileSize)
        {
            return BadRequest(new
            {
                message = "檔案不可超過 20 MB"
            });
        }

        string extension =
            Path.GetExtension(file.FileName).ToLowerInvariant();

        if (!AllowedExtensions.Contains(extension))
        {
            return BadRequest(new
            {
                message = "目前只接受 PDF、PNG、JPG、JPEG 檔案"
            });
        }

        // 建立不對外公開的檔案儲存位置
        string uploadFolder = Path.Combine(
            _environment.ContentRootPath,
            "PrivateUploads");

        Directory.CreateDirectory(uploadFolder);

        // 使用 GUID，保留檔案副檔名
        string storedFileName =
            $"{Guid.NewGuid():N}{extension}";

        string filePath = Path.Combine(
            uploadFolder,
            storedFileName);

        try
        {
            await using (var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await file.CopyToAsync(
                    stream,
                    cancellationToken);
            }

            // 檢查檔案標頭是否符合副檔名
            byte[] header = new byte[8];

            int read;

            await using (var stream =
                System.IO.File.OpenRead(filePath))
            {
                read = await stream.ReadAsync(
                    header.AsMemory(),
                    cancellationToken);
            }

            if (!IsValidFileSignature(
                header.AsSpan(0, read),
                extension))
            {
                System.IO.File.Delete(filePath);

                return BadRequest(new
                {
                    message = "檔案內容與副檔名不符，或檔案格式不正確"
                });
            }

            // 計算 SHA-256
            string sha256;

            await using (var stream =
                System.IO.File.OpenRead(filePath))
            {
                byte[] hash = await SHA256.HashDataAsync(
                    stream,
                    cancellationToken);

                sha256 = Convert.ToHexString(hash);
            }

            string caseId = Guid.NewGuid().ToString("N");

            return Ok(new
            {
                status = "Success",
                message = "病例檔案上傳成功",
                caseId,
                medicalRecordNumber =
                    medicalRecordNumber.Trim(),
                fileName = Path.GetFileName(file.FileName),
                fileSize = file.Length,
                sha256,
                processingStatus = "Uploaded"
            });
        }
        catch
        {
            // 上傳或處理失敗時，清除暫存檔案
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            throw;
        }
    }

    // 驗證 PDF、PNG、JPG、JPEG 的檔案標頭
    private static bool IsValidFileSignature(
        ReadOnlySpan<byte> header,
        string extension)
    {
        return extension switch
        {
            ".pdf" =>
                header.Length >= 5 &&
                header[..5].SequenceEqual(
                    new byte[]
                    {
                        0x25, 0x50, 0x44, 0x46, 0x2D
                    }),

            ".png" =>
                header.Length >= 8 &&
                header[..8].SequenceEqual(
                    new byte[]
                    {
                        0x89, 0x50, 0x4E, 0x47,
                        0x0D, 0x0A, 0x1A, 0x0A
                    }),

            ".jpg" or ".jpeg" =>
                header.Length >= 3 &&
                header[..3].SequenceEqual(
                    new byte[]
                    {
                        0xFF, 0xD8, 0xFF
                    }),

            _ => false
        };
    }
}
