using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

/// <summary>
/// Health check and CORS test controller
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public class HealthController : ControllerBase
{
    /// <summary>
    /// Simple health check endpoint to test CORS and API availability
    /// </summary>
    /// <response code="200">API is healthy and CORS is working</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        return Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTime.UtcNow,
            Message = "1Rad API is running and CORS is configured",
            Environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Unknown"
        });
    }

    /// <summary>
    /// CORS preflight test endpoint
    /// </summary>
    /// <response code="200">CORS preflight successful</response>
    [HttpOptions]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Options()
    {
        return Ok();
    }
}