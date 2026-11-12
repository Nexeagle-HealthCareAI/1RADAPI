using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace _1RadAPI.Controllers;

/// <summary>
/// Settings → Integrations: a centre's admins issue and revoke API keys for
/// outside systems (DICOMweb viewers, the bridge's worklist feed, an HIS reading
/// FHIR). The key is shown once, at creation; 1Rad keeps only its hash.
///
/// GET    api/v1/integration-clients
/// POST   api/v1/integration-clients        { name, scopes: ["dicomweb.read", …] }
/// DELETE api/v1/integration-clients/{id}   (revoke — stops working within a minute)
/// </summary>
[ApiController]
[Authorize(Roles = $"{RoleConstants.AdminDoctor},{RoleConstants.AdminOperator}")]
[Route("api/v1/integration-clients")]
public class IntegrationClientsController : ControllerBase
{
    private const int MaxActiveKeys = 25;

    private readonly IApplicationDbContext _db;
    private readonly IUserContext _user;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;

    public IntegrationClientsController(IApplicationDbContext db, IUserContext user, IMemoryCache cache, IConfiguration config)
    {
        _db = db;
        _user = user;
        _cache = cache;
        _config = config;
    }

    public sealed class CreateIntegrationClientRequest
    {
        public string? Name { get; set; }
        public List<string>? Scopes { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.IntegrationClients.AsNoTracking()
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                c.Id,
                c.Name,
                keyHint = IntegrationKeys.Prefix + c.KeyId + "_…",
                c.Scopes,
                c.CreatedAt,
                c.LastUsedAt,
                c.RevokedAt,
            })
            .ToListAsync(ct);

        var apiBase = InteropUrls.ApiBase(Request, _config);
        return Ok(new
        {
            success = true,
            data = rows.Select(r => new
            {
                r.Id,
                r.Name,
                r.keyHint,
                scopes = r.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries),
                r.CreatedAt,
                r.LastUsedAt,
                r.RevokedAt,
                active = r.RevokedAt == null,
            }),
            endpoints = new
            {
                dicomWeb = $"{apiBase}/api/v1/dicomweb",
                worklist = $"{apiBase}/api/v1/worklist/mwl",
                fhir = $"{apiBase}/api/v1/fhir",
            },
            availableScopes = IntegrationScopes.All,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateIntegrationClientRequest request, CancellationToken ct)
    {
        var name = request?.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            return BadRequest(new { success = false, error = "Give the key a name (up to 100 characters), e.g. \"OHIF viewer\" or \"Bridge worklist\"." });
        var scopes = IntegrationScopes.Normalise(request!.Scopes);
        if (scopes.Count == 0)
            return BadRequest(new { success = false, error = $"Choose at least one scope: {string.Join(", ", IntegrationScopes.All)}." });

        var active = await _db.IntegrationClients.CountAsync(c => c.RevokedAt == null, ct);
        if (active >= MaxActiveKeys)
            return BadRequest(new { success = false, error = $"A centre can have at most {MaxActiveKeys} active keys — revoke one first." });

        var key = IntegrationKeys.Generate();
        var client = new IntegrationClient
        {
            Id = Guid.NewGuid(),
            HospitalId = _user.HospitalId,
            Name = name,
            KeyId = key.KeyId,
            SecretHash = key.SecretHash,
            Scopes = string.Join(",", scopes),
            CreatedByUserId = _user.UserId == Guid.Empty ? null : _user.UserId,
            CreatedAt = DateTime.UtcNow,
        };
        _db.IntegrationClients.Add(client);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            success = true,
            data = new
            {
                client.Id,
                client.Name,
                scopes,
                client.CreatedAt,
                // Shown ONCE. 1Rad stores only a hash and cannot show it again.
                key = key.FullKey,
            },
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var client = await _db.IntegrationClients.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (client == null) return NotFound(new { success = false, error = "Key not found." });
        if (client.RevokedAt == null)
        {
            client.RevokedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        // This instance forgets it now; other API instances within a minute.
        _cache.Remove($"integration-key:{client.KeyId}");
        return Ok(new { success = true });
    }
}
