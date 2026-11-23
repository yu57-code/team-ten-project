using System.Globalization;
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/exceptions")]
public sealed class ExceptionsController(ExceptionService service, ILogger<ExceptionsController> logger)
    : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> GetOpenExceptions(CancellationToken token,
        [FromQuery] ExceptionCategory? category = null) => QueryAsync(async () =>
    {
        var items = await service.GetOpenExceptionsAsync(token);
        return Ok(category.HasValue ? items.Where(e => e.Category == category.Value).ToList() : items);
    }, token);

    [HttpGet("summary")]
    public Task<IActionResult> GetSummary(CancellationToken token) =>
        QueryAsync(async () => Ok(await service.GetSummaryAsync(token)), token);

    [HttpGet("{id}")]
    public Task<IActionResult> GetById(string id, CancellationToken token) => QueryAsync(async () =>
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
            return NotFound(new { message = "找不到異常紀錄" });
        var item = await service.GetByIdAsync(key, token);
        return item is null ? NotFound(new { message = "找不到異常紀錄" }) : Ok(item);
    }, token);

    [HttpPost("{id}/unresolvable")]
    public IActionResult ReadOnlyOperation(string id) =>
        StatusCode(501, new ExceptionOperationResponse(false, "目前僅提供唯讀查詢，處置功能尚未開放。"));

    [HttpPost("{id}/revalidate")]
    [RequestSizeLimit(16384)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Revalidate(string id, [FromBody] MrnRevalidationRequest request,
        [FromServices] MrnRevalidationService revalidation, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
            return NotFound(new { success = false, code = "NOT_AVAILABLE", message = "找不到可重新驗證的異常。" });
        try
        {
            var (result, status) = await revalidation.RevalidateAsync(key, request,
                Request.Headers["X-Mrn-Review-Credential"].ToString(), token);
            return StatusCode(status, result);
        }
        catch (MrnWorkflowException error)
        {
            var message = error.Code switch
            {
                "INVALID_REQUEST" => "重新驗證請求格式無效。",
                "VERSION_CONFLICT" or "STATE_CONFLICT" => "來源或修正版本已變更，請重新載入。",
                "CORRECTION_REQUIRED" => "尚無可重新驗證的人工修正。",
                "REQUEST_CONFLICT" or "HISTORY_CONFLICT" => "請求識別碼已用於不同內容，請重新載入。",
                _ => "此異常目前無法重新驗證。"
            };
            return StatusCode(error.Status, new { success = false, code = error.Code, message });
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException))
        {
            logger.LogWarning("MRN revalidation source integrity check failed ({ErrorType})", error.GetType().Name);
            return Conflict(new { success = false, code = "SOURCE_INTEGRITY_FAILED", message = "保存的來源證據不一致，病例仍維持阻擋。" });
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is NpgsqlException or TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException))
        {
            logger.LogWarning("MRN revalidation failed ({ErrorType})", error.GetType().Name);
            return StatusCode(503, new { success = false, code = "TEMPORARY_FAILURE", message = "重新驗證暫時無法完成；請以相同請求識別碼重試。" });
        }
    }

    [HttpPost("{id}/resolve")]
    [RequestSizeLimit(32768)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Resolve(string id, [FromBody] MrnCorrectionRequest request,
        [FromServices] MrnCorrectionService corrections, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
            return NotFound();
        try
        {
            return Ok(await corrections.SaveAsync(key, request,
                Request.Headers["X-Mrn-Review-Credential"].ToString(), token));
        }
        catch (MrnCorrectionException error)
        {
            return StatusCode(error.StatusCode, new ExceptionOperationResponse(false, error.Message));
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is InvalidDataException or System.Text.Json.JsonException or
                System.Security.Cryptography.CryptographicException))
        {
            return Conflict(new ExceptionOperationResponse(false, "來源或已保存修正版本不一致，請重新載入。"));
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is NpgsqlException or TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException))
        {
            logger.LogWarning("MRN correction save failed ({ErrorType})", error.GetType().Name);
            return StatusCode(503, new ExceptionOperationResponse(false, "人工修正暫時無法保存，請稍後以相同請求識別碼重試。"));
        }
    }

    [HttpGet("{id}/evidence")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetEvidence(string id, [FromServices] MrnEvidenceService evidence,
        CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (!evidence.IsAuthorized(Request.Headers["X-Mrn-Review-Credential"].ToString()))
            return NotFound();
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var key) || key <= 0)
            return NotFound();
        try
        {
            var result = await evidence.ReadAsync(key, token);
            return result is null ? NotFound() : Ok(result);
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is IOException or InvalidDataException or UnauthorizedAccessException or
                System.Text.Json.JsonException or KeyNotFoundException or
                FormatException or System.Security.Cryptography.CryptographicException))
        {
            return Conflict(new { message = "來源證據不完整或版本已變更，請重新載入。" });
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is NpgsqlException or TimeoutException or InvalidOperationException))
        {
            logger.LogWarning("MRN evidence query failed ({ErrorType})", error.GetType().Name);
            return StatusCode(503, new { message = "證據暫時無法讀取。" });
        }
    }

    private async Task<IActionResult> QueryAsync(Func<Task<IActionResult>> query, CancellationToken token)
    {
        try { return await query(); }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is NpgsqlException or TimeoutException or InvalidOperationException))
        {
            logger.LogWarning("Exception queue query failed ({ErrorType})", error.GetType().Name);
            return StatusCode(503, new { message = "異常資料暫時無法讀取，請稍後再試。" });
        }
    }
}
