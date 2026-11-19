
using ImagingAI.Api.Models;
using ImagingAI.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/exceptions")]
public sealed class ExceptionsController : ControllerBase
{
    private readonly ExceptionService _service;

    public ExceptionsController(ExceptionService service)
    {
        _service = service;
    }

    // 1. 取得所有未結案異常
    // GET /api/exceptions
    [HttpGet]
    public IActionResult GetOpenExceptions(
        [FromQuery] ExceptionCategory? category = null)
    {
        var items = _service.GetOpenExceptions();

        if (category.HasValue)
        {
            items = items
                .Where(e => e.Category == category.Value)
                .ToList();
        }

        return Ok(items);
    }

    // 2. 取得各分類統計
    // GET /api/exceptions/summary
    [HttpGet("summary")]
    public IActionResult GetSummary()
    {
        return Ok(_service.GetSummary());
    }

    // 3. 取得單筆異常詳細
    // GET /api/exceptions/EX-001
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        var item = _service.GetById(id);

        if (item is null)
        {
            return NotFound(new
            {
                message = "找不到異常紀錄"
            });
        }

        return Ok(item);
    }

    // 4. 儲存人工修正
    // POST /api/exceptions/EX-001/resolve
    [HttpPost("{id}/resolve")]
    public IActionResult Resolve(
        string id,
        [FromBody] ResolveExceptionRequest request)
    {
        if (request.Resolution is null)
        {
            return BadRequest(new
            {
                message = "請提供修正資料"
            });
        }

        var result = _service.Resolve(
            id,
            request.Resolution,
            request.Actor);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    // 5. 重新驗證
    // POST /api/exceptions/EX-001/revalidate
    [HttpPost("{id}/revalidate")]
    public IActionResult Revalidate(
        string id,
        [FromBody] RevalidateExceptionRequest request)
    {
        var result = _service.Revalidate(
            id,
            request.Actor);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    // 6. 標記無法處理
    // POST /api/exceptions/EX-001/unresolvable
    [HttpPost("{id}/unresolvable")]
    public IActionResult MarkUnresolvable(
        string id,
        [FromBody] MarkUnresolvableRequest request)
    {
        var result = _service.MarkUnresolvable(
            id,
            request.Note,
            request.Actor);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }
}
