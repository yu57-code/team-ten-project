using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController(ReviewService service, ILogger<ReviewsController> logger)
    : ControllerBase
{
    [HttpGet("pending-ids")]
    public Task<IActionResult> GetPendingIds(CancellationToken token) => ExecuteAsync(async () =>
        Ok(await service.GetPendingReviewIdsAsync(token)), token);

    [HttpGet("next")]
    public Task<IActionResult> GetNextPending(CancellationToken token) => ExecuteAsync(async () =>
    {
        var detail = await service.GetReviewAsync(null, token);
        return detail is null ? NotFound(new { message = "目前沒有待覆核的病例" }) : Ok(detail);
    }, token);

    [HttpGet("{caseId}")]
    public Task<IActionResult> GetByCaseId(string caseId, CancellationToken token) => ExecuteAsync(async () =>
    {
        var detail = await service.GetReviewAsync(caseId, token);
        return detail is null ? NotFound(new { message = $"找不到病例 {caseId}" }) : Ok(detail);
    }, token);

    [HttpPut("{caseId}/draft")]
    public Task<IActionResult> SaveDraft(string caseId, [FromBody] SaveReviewRequest request, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var result = await service.SaveDraftAsync(caseId, request, token);
            return result.Succeeded ? Ok(result) : BadRequest(result);
        }, token);

    [HttpPost("{caseId}/submit")]
    public Task<IActionResult> Submit(string caseId, [FromBody] SaveReviewRequest request, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var result = await service.SubmitReviewAsync(caseId, request, token);
            return result.Succeeded ? Ok(result) : BadRequest(result);
        }, token);

    [HttpPost("{caseId}/return")]
    public Task<IActionResult> ReturnToException(string caseId, [FromBody] ReturnReviewRequest request, CancellationToken token) =>
        ExecuteAsync(async () =>
        {
            var result = await service.ReturnToExceptionAsync(caseId, request, token);
            return result.Succeeded ? Ok(result) : BadRequest(result);
        }, token);

    [HttpGet("{caseId}/fhir")]
    public Task<IActionResult> ExportFhir(string caseId, CancellationToken token) => ExecuteAsync(async () =>
    {
        var json = await service.ExportFhirBundleAsync(caseId, token);
        return json is null
            ? NotFound(new { message = "尚未產生該病例的 FHIR Bundle" })
            : Content(json, "application/json; charset=utf-8");
    }, token);

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action, CancellationToken token)
    {
        try
        {
            return await action();
        }
        catch (Exception error) when (!token.IsCancellationRequested &&
            (error is NpgsqlException or TimeoutException or InvalidOperationException))
        {
            logger.LogWarning("Review API operation failed ({ErrorType})", error.GetType().Name);
            return StatusCode(503, new ReviewOperationResponse(false, "資料庫暫時無法存取，請稍後再試。"));
        }
    }
}