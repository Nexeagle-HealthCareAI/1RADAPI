using System.Text;
using System.Text.Json.Nodes;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Infrastructure.Interop;
using _1RadAPI.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace _1RadAPI.Controllers;

/// <summary>
/// DICOMweb (PS3.18) over 1Rad's Cloud PACS — gap analysis P3-6, phases 1–2.
/// Lets OHIF, Weasis, AI tools and other PACS search, view and send images
/// without the 1Rad web app or the bridge.
///
/// Base URL: {api}/api/v1/dicomweb
///   QIDO-RS  GET  studies | series | studies/{s}/series | studies/{s}/instances
///                 | studies/{s}/series/{se}/instances
///   WADO-RS  GET  studies/{s} | …/series/{se} | …/instances/{i}       (multipart application/dicom)
///                 …/metadata at each level                               (application/dicom+json)
///                 …/instances/{i}/frames/{n[,n…]}                         (multipart, as stored or uncompressed)
///                 …/rendered, …/frames/{n}/rendered, …/thumbnail          (image/jpeg or image/png)
///   STOW-RS  POST studies | studies/{s}       (multipart/related; type="application/dicom" or "application/dicom+json" + bulk data)
///
/// Auth: a 1Rad staff access token, or a centre integration key with scope
/// dicomweb.read / dicomweb.write (see IntegrationKeyAuthenticationHandler).
/// </summary>
[ApiController]
[Authorize]
[RequiresModule(ModuleConstants.Pacs)]
[Route("api/v1/dicomweb")]
public class DicomWebController : ControllerBase
{
    private const string DicomJson = "application/dicom+json";
    private const long MaxStowBytes = 1_073_741_824L;     // 1 GB per request, same as other uploads
    private const int MaxFramesPerRequest = 1000;

    private readonly DicomWebService _dicomWeb;
    private readonly DicomStowService _stow;
    private readonly IUserContext _user;
    private readonly IConfiguration _config;
    private readonly ILogger<DicomWebController> _logger;

    public DicomWebController(
        DicomWebService dicomWeb, DicomStowService stow, IUserContext user, IConfiguration config, ILogger<DicomWebController> logger)
    {
        _dicomWeb = dicomWeb;
        _stow = stow;
        _user = user;
        _config = config;
        _logger = logger;
    }

    private string BaseUrl => InteropUrls.ApiBase(Request, _config) + "/api/v1/dicomweb";

    private QidoQuery Qido() =>
        QidoQuery.Parse(Request.Query.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value.ToString())));

    private IActionResult QidoResult(JsonArray? result)
    {
        if (result == null) return NotFound();
        if (result.Count == 0) return NoContent(); // PS3.18: no matches → 204
        return Content(result.ToJsonString(), DicomJson);
    }

    // ── QIDO-RS ─────────────────────────────────────────────────────────────

    [HttpGet("studies")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchStudies(CancellationToken ct) =>
        QidoResult(await _dicomWeb.SearchStudiesAsync(Qido(), BaseUrl, ct));

    [HttpGet("series")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchAllSeries(CancellationToken ct) =>
        QidoResult(await _dicomWeb.SearchSeriesAsync(null, Qido(), BaseUrl, ct));

    [HttpGet("studies/{study}/series")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchSeries(string study, CancellationToken ct) =>
        QidoResult(await _dicomWeb.SearchSeriesAsync(study, Qido(), BaseUrl, ct));

    [HttpGet("instances")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchAllInstances(CancellationToken ct)
    {
        var q = Qido();
        if (q.StudyInstanceUids.Count == 0)
            return BadRequest(new { error = "Searching instances across all studies needs a StudyInstanceUID parameter." });
        return QidoResult(await _dicomWeb.SearchInstancesAsync(null, null, q, BaseUrl, ct) ?? new JsonArray());
    }

    [HttpGet("studies/{study}/instances")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchStudyInstances(string study, CancellationToken ct) =>
        QidoResult(await _dicomWeb.SearchInstancesAsync(study, null, Qido(), BaseUrl, ct));

    [HttpGet("studies/{study}/series/{series}/instances")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> SearchSeriesInstances(string study, string series, CancellationToken ct) =>
        QidoResult(await _dicomWeb.SearchInstancesAsync(study, series, Qido(), BaseUrl, ct));

    // ── WADO-RS: metadata ───────────────────────────────────────────────────

    [HttpGet("studies/{study}/metadata")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> StudyMetadata(string study, CancellationToken ct) => Metadata(study, null, null, ct);

    [HttpGet("studies/{study}/series/{series}/metadata")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> SeriesMetadata(string study, string series, CancellationToken ct) => Metadata(study, series, null, ct);

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/metadata")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> InstanceMetadata(string study, string series, string instance, CancellationToken ct) =>
        Metadata(study, series, instance, ct);

    private async Task<IActionResult> Metadata(string study, string? series, string? instance, CancellationToken ct)
    {
        var list = await _dicomWeb.ResolveAsync(study, series, instance, null, ct);
        if (list == null || list.Count == 0) return NotFound();
        var json = await _dicomWeb.MetadataAsync(list, BaseUrl, ct);
        return Content(json.ToJsonString(), DicomJson);
    }

    // ── WADO-RS: instances ──────────────────────────────────────────────────

    [HttpGet("studies/{study}")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> RetrieveStudy(string study, CancellationToken ct) => Retrieve(study, null, null, ct);

    [HttpGet("studies/{study}/series/{series}")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> RetrieveSeries(string study, string series, CancellationToken ct) => Retrieve(study, series, null, ct);

    [HttpGet("studies/{study}/series/{series}/instances/{instance}")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> RetrieveInstance(string study, string series, string instance, CancellationToken ct) =>
        Retrieve(study, series, instance, ct);

    private async Task<IActionResult> Retrieve(string study, string? series, string? instance, CancellationToken ct)
    {
        var wanted = DicomWebAccept.InstanceSyntax(Request.Headers.Accept.ToString(), DicomWebAccept.AnySyntax);
        if (wanted == null) return StatusCode(StatusCodes.Status406NotAcceptable);

        var list = await _dicomWeb.ResolveAsync(study, series, instance, null, ct);
        if (list == null || list.Count == 0) return NotFound();

        var baseUrl = BaseUrl;
        var partType = "application/dicom";
        await WriteMultipartAsync(partType, ReadAhead(list, i => _dicomWeb.ReadInstanceAsync(i, wanted, baseUrl, ct), 4, ct), ct);
        return new EmptyResult();
    }

    // ── WADO-RS: frames ─────────────────────────────────────────────────────

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/frames/{frames}")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public async Task<IActionResult> RetrieveFrames(string study, string series, string instance, string frames, CancellationToken ct)
    {
        var numbers = new List<int>();
        foreach (var part in frames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var n) || n < 1) return BadRequest(new { error = $"Bad frame number '{part}'." });
            numbers.Add(n);
        }
        if (numbers.Count == 0 || numbers.Count > MaxFramesPerRequest) return BadRequest(new { error = "Ask for 1–1000 frames." });

        var list = await _dicomWeb.ResolveAsync(study, series, instance, null, ct);
        if (list == null || list.Count == 0) return NotFound();

        var (parts, notAcceptable) = await _dicomWeb.ReadFramesAsync(list[0], numbers, Request.Headers.Accept.ToString(), ct);
        if (notAcceptable) return StatusCode(StatusCodes.Status406NotAcceptable);
        if (parts == null) return NotFound(new { error = "Frame not found in this instance." });

        var partType = parts[0].ContentType.Split(';')[0].Trim();
        await WriteMultipartAsync(partType, ToAsync(parts), ct);
        return new EmptyResult();
    }

    // ── WADO-RS: rendered and thumbnail ─────────────────────────────────────
    // Query: quality=1..100, viewport=width,height, window=center,width[,function].
    // Accept: image/jpeg (default) or image/png.

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/rendered")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> RenderedInstance(string study, string series, string instance, CancellationToken ct) =>
        Rendered(study, series, instance, "1", ct);

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/frames/{frames}/rendered")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> RenderedFrame(string study, string series, string instance, string frames, CancellationToken ct) =>
        Rendered(study, series, instance, frames, ct);

    private async Task<IActionResult> Rendered(string study, string series, string instance, string frames, CancellationToken ct)
    {
        if (!int.TryParse(frames.Split(',')[0].Trim(), out var frame) || frame < 1 || frames.Contains(','))
            return BadRequest(new { error = "Render one frame at a time (frames/{n}/rendered)." });
        var options = RenderOptions();
        if (options == null) return StatusCode(StatusCodes.Status406NotAcceptable);

        var list = await _dicomWeb.ResolveAsync(study, series, instance, null, ct);
        if (list == null || list.Count == 0) return NotFound();
        var image = await _dicomWeb.RenderAsync(list[0], frame - 1, options, ct);
        return image == null ? NotFound(new { error = "This frame can't be rendered as an image." }) : File(image, options.MediaType);
    }

    [HttpGet("studies/{study}/thumbnail")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> StudyThumbnail(string study, CancellationToken ct) => Thumbnail(study, null, null, ct);

    [HttpGet("studies/{study}/series/{series}/thumbnail")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> SeriesThumbnail(string study, string series, CancellationToken ct) => Thumbnail(study, series, null, ct);

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/thumbnail")]
    [IntegrationScope(IntegrationScopes.DicomWebRead)]
    public Task<IActionResult> InstanceThumbnail(string study, string series, string instance, CancellationToken ct) =>
        Thumbnail(study, series, instance, ct);

    private async Task<IActionResult> Thumbnail(string study, string? series, string? instance, CancellationToken ct)
    {
        var options = RenderOptions();
        if (options == null) return StatusCode(StatusCodes.Status406NotAcceptable);
        var list = await _dicomWeb.ResolveAsync(study, series, instance, null, ct);
        if (list == null || list.Count == 0) return NotFound();
        var image = await _dicomWeb.ThumbnailAsync(list, options, ct);
        if (image == null) return NotFound(new { error = "No thumbnail could be made for this image." });
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(image, options.MediaType);
    }

    private DicomRenderer.RenderOptions? RenderOptions()
    {
        var media = DicomRenderer.MediaType(Request.Headers.Accept.ToString());
        if (media == null) return null;
        return DicomRenderer.ParseQuery(Request.Query["quality"], Request.Query["viewport"], Request.Query["window"], media);
    }

    // ── STOW-RS ─────────────────────────────────────────────────────────────

    [HttpPost("studies")]
    [DisableRequestSizeLimit]
    [IntegrationScope(IntegrationScopes.DicomWebWrite)]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Doctor},{RoleConstants.Technician},{IntegrationScopes.Role}")]
    public Task<IActionResult> Store(CancellationToken ct) => StoreInternal(null, ct);

    [HttpPost("studies/{study}")]
    [DisableRequestSizeLimit]
    [IntegrationScope(IntegrationScopes.DicomWebWrite)]
    [Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator},{RoleConstants.Doctor},{RoleConstants.Technician},{IntegrationScopes.Role}")]
    public Task<IActionResult> StoreToStudy(string study, CancellationToken ct) => StoreInternal(study, ct);

    private async Task<IActionResult> StoreInternal(string? study, CancellationToken ct)
    {
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/related", StringComparison.OrdinalIgnoreCase))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                new { error = "Send multipart/related; type=\"application/dicom\"." });

        var type = (mediaType.Parameters.FirstOrDefault(p => p.Name.Equals("type", StringComparison.OrdinalIgnoreCase))?.Value.ToString().Trim('"')
                    ?? string.Empty).ToLowerInvariant();
        var jsonForm = type == "application/dicom+json";
        if (type.Length > 0 && type != "application/dicom" && !jsonForm)
            return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                new { error = "Send type=\"application/dicom\" or type=\"application/dicom+json\" (with bulk data parts)." });

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary)) return BadRequest(new { error = "Missing multipart boundary." });

        var parts = new List<byte[]>();
        var jsonParts = new List<string>();
        var bulkParts = new List<DicomJsonStowAssembler.BulkPart>();
        long total = 0;
        var reader = new MultipartReader(boundary, Request.Body) { BodyLengthLimit = MaxStowBytes };
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) != null)
        {
            var partType = (section.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
            using var ms = new MemoryStream();
            await section.Body.CopyToAsync(ms, ct);
            total += ms.Length;
            if (total > MaxStowBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Send at most 1 GB per request." });

            if (partType is "" or "application/dicom")
                parts.Add(ms.ToArray());
            else if (partType is "application/dicom+json" or "application/json")
                jsonParts.Add(Encoding.UTF8.GetString(ms.ToArray()));
            else
            {
                string? location = null;
                if (section.Headers != null && section.Headers.TryGetValue("Content-Location", out var loc)) location = loc.ToString();
                bulkParts.Add(new DicomJsonStowAssembler.BulkPart(location, section.ContentType, ms.ToArray()));
            }
        }

        var assembleFailures = 0;
        if (jsonParts.Count > 0)
        {
            var assembled = DicomJsonStowAssembler.Assemble(jsonParts, bulkParts);
            parts.AddRange(assembled.Files);
            assembleFailures = assembled.Failed;
            foreach (var e in assembled.Errors.Take(5)) _logger.LogWarning("[STOW] JSON dataset refused: {Error}", e);
        }
        if (parts.Count == 0)
            return BadRequest(new
            {
                error = jsonForm
                    ? "No dataset could be built from the DICOM JSON and bulk data parts (check BulkDataURI / Content-Location)."
                    : "No application/dicom parts in the request.",
            });

        var result = await _stow.StoreAsync(parts, study, _user.HospitalId, BaseUrl, ct, assembleFailures);
        _logger.LogInformation("[STOW] centre {Hospital}: stored {Stored}, failed {Failed}", _user.HospitalId, result.Stored, result.Failed);
        return new ContentResult
        {
            StatusCode = result.StatusCode,
            Content = result.Body.ToJsonString(),
            ContentType = DicomJson,
        };
    }

    // ── multipart writing ───────────────────────────────────────────────────

    private async Task WriteMultipartAsync(string partType, IAsyncEnumerable<DicomWebPart> parts, CancellationToken ct)
    {
        var boundary = "1rad-" + Guid.NewGuid().ToString("N");
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = $"multipart/related; type=\"{partType}\"; boundary={boundary}";
        var body = Response.Body;
        try
        {
            await foreach (var part in parts.WithCancellation(ct))
            {
                var header = new StringBuilder()
                    .Append("--").Append(boundary).Append("\r\n")
                    .Append("Content-Type: ").Append(part.ContentType).Append("\r\n");
                if (part.Location != null) header.Append("Content-Location: ").Append(part.Location).Append("\r\n");
                header.Append("\r\n");
                await body.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), ct);
                await body.WriteAsync(part.Bytes, ct);
                await body.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), ct);
            }
            await body.WriteAsync(Encoding.ASCII.GetBytes($"--{boundary}--\r\n"), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Headers are already sent; the best signal left is to cut the response short.
            _logger.LogError(ex, "[DICOMWEB] retrieve failed mid-stream");
            HttpContext.Abort();
        }
    }

    /// <summary>Fetch up to <paramref name="window"/> parts ahead while keeping their order.</summary>
    private static async IAsyncEnumerable<DicomWebPart> ReadAhead(
        List<DicomWebInstance> items, Func<DicomWebInstance, Task<DicomWebPart>> read, int window,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var pending = new Queue<Task<DicomWebPart>>();
        var next = 0;
        while (next < items.Count || pending.Count > 0)
        {
            while (next < items.Count && pending.Count < window)
                pending.Enqueue(read(items[next++]));
            ct.ThrowIfCancellationRequested();
            yield return await pending.Dequeue();
        }
    }

    private static async IAsyncEnumerable<DicomWebPart> ToAsync(IEnumerable<DicomWebPart> parts)
    {
        foreach (var p in parts) yield return p;
        await Task.CompletedTask;
    }
}
