using System.Text;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Calls Claude (Anthropic Messages API). Config read from the "Anthropic"
/// section (App Service Configuration / appsettings / env vars, same as other
/// secrets):
///   Anthropic:ApiKey  — secret API key (required)
///   Anthropic:Model   — model id (default "claude-haiku-4-5")
/// </summary>
public class AnthropicService : IAnthropicService
{
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ILogger<AnthropicService> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    private const string Placeholder = "YOUR_LOCAL_DEV_ANTHROPIC_API_KEY";

    public AnthropicService(HttpClient http, IConfiguration configuration, ILogger<AnthropicService> logger)
    {
        _http = http;
        _logger = logger;
        var section = configuration.GetSection("Anthropic");
        // Trim — pipeline/library secrets frequently arrive with a trailing
        // newline or surrounding quotes, which makes Anthropic reject the key
        // with 401 even though the value "looks" right.
        _apiKey = (section["ApiKey"] ?? string.Empty).Trim().Trim('"');
        _model = (section["Model"] ?? "claude-haiku-4-5").Trim();
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Anthropic API key is not configured (set Anthropic__ApiKey).");
        if (_apiKey == Placeholder)
            throw new InvalidOperationException("Anthropic API key is still the placeholder — set the real Anthropic__ApiKey in App Service configuration.");

        // SAFE diagnostic — never logs the key itself, only shape signals so we
        // can tell from the log stream whether a real key is being read.
        _logger.LogInformation("[Anthropic] using key: len={Len}, startsWithSkAnt={SkAnt}, model={Model}",
            _apiKey.Length, _apiKey.StartsWith("sk-ant-"), _model);

        var payload = new
        {
            model = _model,
            max_tokens = 2500,
            temperature = 0.2,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl);
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Anthropic] {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Anthropic API returned {(int)response.StatusCode}.");
        }

        // Response shape: { "content": [ { "type": "text", "text": "..." }, ... ], ... }
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && block.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
        }
        return sb.ToString();
    }
}
