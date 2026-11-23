using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/reviews")]
public sealed class ReviewsController(
    ReviewService reviewService) : ControllerBase
{
    [HttpGet("pending")]
    public async Task<IActionResult> GetPending(
        CancellationToken cancellationToken)
    {
        var reports = await reviewService.GetPendingAsync(
            cancellationToken);

        return Ok(reports);
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(
        long id,
        CancellationToken cancellationToken)
    {
        var report = await reviewService.GetByIdAsync(
            id,
            cancellationToken);

        if (report is null)
            return NotFound();

        return Ok(report);
    }

    [HttpGet("{id:long}/fields")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetFields(long id)
    {
        // Closed until the API can verify reviewer identity and access to this report.
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpGet("{id:long}/preview/{page:int}")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetPreview(long id, int page)
    {
        // Closed until reviewer identity and access to the report can be verified.
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status403Forbidden);
    }

    [HttpGet("{id:long}/draft")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult GetDraft(long id) => DenyDraftAccess();

    [HttpPut("{id:long}/draft")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult PutDraft(long id) => DenyDraftAccess();

    [HttpPost("{id:long}/confirm")]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Confirm(long id) => DenyDraftAccess();

    private IActionResult DenyDraftAccess()
    {
        // Enable draft access only after reviewer identity and case authorization are available.
        Response.Headers.CacheControl = "no-store";
        return StatusCode(StatusCodes.Status403Forbidden);
    }
}
