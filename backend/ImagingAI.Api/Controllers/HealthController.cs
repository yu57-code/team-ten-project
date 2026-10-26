using Microsoft.AspNetCore.Mvc;

namespace ImagingAI.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        // GET: api/health
        [HttpGet]
        public IActionResult GetHealth()
        {
            return Ok(new
            {
                status = "Success",
                message = "ImagingAI API is running",
                service = "ImagingAI.Api",
                timestamp = DateTime.UtcNow
            });
        }
    }
}