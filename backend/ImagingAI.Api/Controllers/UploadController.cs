
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/upload")]
public class UploadController : ControllerBase
{
    private const long MaxFileSize = 20 * 1024 * 1024;

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
            _environment.ContentRootPath,
            "PrivateUploads");

        Directory.CreateDirectory(uploadFolder);

        // 使用隨機名稱，避免原始檔名造成路徑問題
        string storedFileName =
            $"{Guid.NewGuid():N}.pdf";

        string filePath = Path.Combine(
            uploadFolder,
            storedFileName);

        try
        {
            await using (var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write))
            {
                await file.CopyToAsync(
                    stream,
                    cancellationToken);
            }

            // 檢查 PDF 檔案標頭
            byte[] header = new byte[5];

            await using (var stream = System.IO.File.OpenRead(filePath))
            {
                int read = await stream.ReadAsync(
                    header,
                    cancellationToken);

                if (read != 5 ||
                    System.Text.Encoding.ASCII.GetString(header)
                        != "%PDF-")
                {
                    System.IO.File.Delete(filePath);

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
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            throw;
        }
    }
}
