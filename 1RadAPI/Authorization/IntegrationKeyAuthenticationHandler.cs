using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using _1Rad.Application.Common;
using _1Rad.Domain.Constants;
using _1Rad.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Authorization;

/// <summary>
/// Signs in an outside system with a centre's integration key
/// (<see cref="_1Rad.Domain.Entities.IntegrationClient"/>). Accepted as
///   Authorization: Bearer 1rk_…            (FHIR / most DICOMweb clients)
///   Authorization: Basic base64(any:1rk_…)  (Orthanc, Weasis, curl -u)
///   X-Api-Key: 1rk_…
/// and ONLY on the integration endpoints — a key can never call the rest of the API.
///
/// The resulting identity carries the centre ("cid", "hubs"), type "integration",
/// role "Integration" and one "scope" claim per granted scope. Session checks and
/// the custom-role sentinel skip it (they are for staff sign-ins); the
/// subscription lock still applies.
/// </summary>
public sealed class IntegrationKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IntegrationKey";
    /// <summary>Policy scheme that picks this handler or JWT per request.</summary>
    public const string SelectorScheme = "JwtOrIntegrationKey";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LastUsedWriteEvery = TimeSpan.FromMinutes(5);

    public IntegrationKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    private sealed record CachedClient(Guid Id, Guid HospitalId, string Name, string SecretHash, string Scopes, DateTime? RevokedAt);

    /// <summary>True when the request carries something shaped like an integration key.</summary>
    public static bool HasIntegrationCredentials(HttpRequest request) => ExtractKey(request) != null;

    public static bool IsIntegrationPath(PathString path) =>
        IntegrationScopes.PathPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = ExtractKey(Request);
        if (key == null) return AuthenticateResult.NoResult();
        if (!IsIntegrationPath(Request.Path))
            return AuthenticateResult.Fail("Integration keys are accepted only on the DICOMweb, worklist and FHIR endpoints.");

        var keyId = IntegrationKeys.TryGetKeyId(key);
        if (keyId == null) return AuthenticateResult.Fail("Malformed integration key.");

        var cache = Context.RequestServices.GetRequiredService<IMemoryCache>();
        var client = await cache.GetOrCreateAsync($"integration-key:{keyId}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            var db = Context.RequestServices.GetRequiredService<ApplicationDbContext>();
            // No centre is known yet, so the tenant filter is bypassed for this one lookup.
            return await db.IntegrationClients.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.KeyId == keyId)
                .Select(c => new CachedClient(c.Id, c.HospitalId, c.Name, c.SecretHash, c.Scopes, c.RevokedAt))
                .FirstOrDefaultAsync(Context.RequestAborted);
        });

        if (client == null || client.RevokedAt != null || !IntegrationKeys.Matches(key, client.SecretHash))
            return AuthenticateResult.Fail("Invalid or revoked integration key.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.Id.ToString()),
            new("sub", client.Id.ToString()),
            new(ClaimTypes.Name, client.Name),
            new("cid", client.HospitalId.ToString()),
            new("hubs", client.HospitalId.ToString()),
            new("type", IntegrationScopes.PrincipalType),
            new(ClaimTypes.Role, IntegrationScopes.Role),
        };
        foreach (var scope in IntegrationScopes.Normalise(client.Scopes.Split(',')))
            claims.Add(new Claim(IntegrationScopes.ScopeClaim, scope));

        await TouchLastUsedAsync(cache, client.Id);

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", "Basic realm=\"1Rad integration\"");
        return Task.CompletedTask;
    }

    private async Task TouchLastUsedAsync(IMemoryCache cache, Guid clientId)
    {
        var flag = $"integration-key-used:{clientId:N}";
        if (cache.TryGetValue(flag, out _)) return;
        cache.Set(flag, true, LastUsedWriteEvery);
        try
        {
            var db = Context.RequestServices.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            await db.IntegrationClients.IgnoreQueryFilters()
                .Where(c => c.Id == clientId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastUsedAt, now), Context.RequestAborted);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not record integration key use for {ClientId}", clientId);
        }
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var apiKey = request.Headers["X-Api-Key"].ToString().Trim();
        if (IntegrationKeys.LooksLikeKey(apiKey)) return apiKey;

        var auth = request.Headers.Authorization.ToString().Trim();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = auth.Substring(7).Trim();
            return IntegrationKeys.LooksLikeKey(token) ? token : null;
        }
        if (auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Substring(6).Trim()));
                var sep = decoded.IndexOf(':');
                var user = sep >= 0 ? decoded.Substring(0, sep) : decoded;
                var pass = sep >= 0 ? decoded.Substring(sep + 1) : string.Empty;
                if (IntegrationKeys.LooksLikeKey(pass)) return pass;
                if (IntegrationKeys.LooksLikeKey(user)) return user;
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;
    }
}
