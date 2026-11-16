using ImagingAI.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ImagingAI.Api.Controllers;

[ApiController]
[Route("api/database-test")]
public sealed class DatabaseTestController(
    ImagingAiDbContext dbContext,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return NotFound();

        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "error",
                databaseConnected = false,
                patientTableAccessible = false,
                message = "無法連接 PostgreSQL 資料庫"
            });
        }

        try
        {
            var patientCount = await dbContext.Patients.CountAsync(cancellationToken);
            return Ok(new
            {
                status = "success",
                databaseConnected = true,
                patientTableAccessible = true,
                patientCount,
                message = "PostgreSQL 資料庫連線及查詢成功"
            });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            var message = (exception.GetBaseException() as PostgresException)?.SqlState switch
            {
                PostgresErrorCodes.InsufficientPrivilege => "資料庫帳號缺少 clinical Schema 或 patient 資料表的查詢權限",
                PostgresErrorCodes.UndefinedTable => "目前連線的資料庫找不到 clinical.patient",
                _ => "已連接 PostgreSQL，但無法查詢病患資料表"
            };
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                status = "error",
                databaseConnected = true,
                patientTableAccessible = false,
                message
            });
        }
    }
}
