using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// Tiny utility endpoint group. Currently used by the offline-first sync
// engine to measure clock skew (the client's Date.now() can be hours
// wrong on a laptop that's been suspended for a while; every "updatedAfter"
// filter the client sends would otherwise drift with it).
//
// AllowAnonymous so the sync engine can re-measure skew even before login
// completes — clock skew is not a secret.
[ApiController]
[AllowAnonymous]
[Route("api/v1/system")]
public class SystemController : ControllerBase
{
    [HttpGet("now")]
    public IActionResult Now()
    {
        // ISO-8601 with a 'Z' suffix so the JS Date parser unambiguously
        // treats it as UTC (the rest of the codebase had to add a
        // parseUtc() helper because EF returns Kind=Unspecified strings
        // without the Z — we don't repeat that mistake here).
        return Ok(new
        {
            utcNow = DateTime.UtcNow.ToString("o"),
        });
    }
}
