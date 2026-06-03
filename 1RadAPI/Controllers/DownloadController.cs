using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers;

// Serves the packaged desktop application installer. Anonymous because the
// "Get Desktop App" link is a plain browser navigation (no bearer header), and
// the installer itself is not sensitive. The physical installer is produced by
// `electron-builder` and dropped at the configured path (or wwwroot/downloads)
// by the release pipeline.
[AllowAnonymous]
[ApiController]
[Route("api/v1/download")]
public class DownloadController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    public DownloadController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    /// <summary>
    /// Streams the Windows desktop installer (1Rad-Setup.exe). The path is
    /// configurable via DesktopApp:InstallerPath; otherwise it defaults to
    /// wwwroot/downloads/1Rad-Setup.exe. Returns 404 with a friendly message
    /// until the installer has been built and placed there.
    /// </summary>
    [HttpGet("desktop")]
    public IActionResult Desktop()
    {
        // Preferred in cloud: the release pipeline uploads the installer to
        // Blob Storage and we just redirect there. Keeps large binaries off
        // the API host while the link the client uses stays on our domain.
        var installerUrl = _config["DesktopApp:InstallerUrl"];
        if (!string.IsNullOrWhiteSpace(installerUrl))
        {
            return Redirect(installerUrl);
        }

        // Fallback: serve a local file (handy for on-prem / single-box setups).
        var configured = _config["DesktopApp:InstallerPath"];
        var path = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(_env.ContentRootPath, "wwwroot", "downloads", "1Rad-Setup.exe");

        if (!System.IO.File.Exists(path))
        {
            return NotFound(new { message = "The desktop installer is not available yet. Please check back soon." });
        }

        var fileName = Path.GetFileName(path);
        var stream = System.IO.File.OpenRead(path);
        // application/octet-stream + a file name → the browser downloads it.
        return File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
    }
}
