namespace _1RadAPI.Controllers;

/// <summary>
/// The public base URL written into DICOMweb retrieve URLs and FHIR fullUrls.
/// Set <c>Interop:PublicApiBaseUrl</c> (e.g. https://1rad-api.nexeagle.com) when
/// the API sits behind a proxy; otherwise it is taken from the request, honouring
/// X-Forwarded-Proto / X-Forwarded-Host from Caddy / nginx.
/// </summary>
internal static class InteropUrls
{
    public static string ApiBase(HttpRequest request, IConfiguration config)
    {
        var configured = config["Interop:PublicApiBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

        var proto = request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        var host = request.Headers["X-Forwarded-Host"].ToString().Split(',')[0].Trim();
        var scheme = proto is "http" or "https" ? proto : request.Scheme;
        var authority = host.Length > 0 ? host : request.Host.Value;
        return $"{scheme}://{authority}{request.PathBase}";
    }
}
