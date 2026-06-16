using System.Net.Http;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Proxies grammar checks to a self-hosted LanguageTool server. Keeps PHI on the
/// organisation's network (unlike the public api.languagetool.org the editor used
/// to call directly). Config:
///   LanguageTool:BaseUrl  — e.g. http://languagetool:8010  (unset = disabled)
/// </summary>
public class LanguageToolService : ILanguageToolService
{
    private readonly HttpClient _http;
    private readonly ILogger<LanguageToolService> _logger;
    private readonly string _baseUrl;

    public LanguageToolService(HttpClient http, IConfiguration configuration, ILogger<LanguageToolService> logger)
    {
        _http = http;
        _logger = logger;
        // Trim trailing slash + stray quotes/whitespace that pipeline secrets often carry.
        _baseUrl = (configuration["LanguageTool:BaseUrl"] ?? string.Empty).Trim().Trim('"').TrimEnd('/');
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_baseUrl);

    public async Task<string> CheckAsync(string text, string language, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("LanguageTool base URL is not configured.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["text"] = text ?? string.Empty,
            ["language"] = string.IsNullOrWhiteSpace(language) ? "en-US" : language,
            ["enabledOnly"] = "false",
        });

        using var resp = await _http.PostAsync($"{_baseUrl}/v2/check", form, cancellationToken);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cancellationToken);
    }
}
