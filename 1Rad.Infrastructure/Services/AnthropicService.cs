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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && _apiKey != Placeholder;

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        => (await SendAsync(systemPrompt, userPrompt, cancellationToken)).Text;

    public async Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default)
        => (await GenerateJsonWithUsageAsync(systemPrompt, userPrompt, responseSchema, cancellationToken)).Text;

    public async Task<AiJsonResult> GenerateJsonWithUsageAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default)
    {
        // Claude has no Gemini-style "JSON mode" flag, so steer it via the prompt:
        // demand a single JSON object (callers strip fences + parse leniently).
        var sys = new StringBuilder(systemPrompt);
        sys.AppendLine();
        sys.AppendLine();
        sys.AppendLine("OUTPUT FORMAT: Respond with a SINGLE valid JSON object and NOTHING else — no prose, no explanation, no markdown code fences.");
        if (responseSchema is not null)
        {
            sys.AppendLine("The JSON must conform to this schema:");
            sys.AppendLine(JsonSerializer.Serialize(responseSchema));
        }
        var (text, usage) = await SendAsync(sys.ToString(), userPrompt, cancellationToken);
        return new AiJsonResult(text, usage);
    }

    private async Task<(string Text, AiUsage Usage)> SendAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
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
            // 4096 covers a full HTML report (template + filled content). 2500
            // truncated mid-output for medium/long radiology templates.
            max_tokens = 4096,
            temperature = 0.2,
            // Prompt caching: send the (large, static) system prompt as a cached
            // content block. Calls that share this prefix within the cache window
            // are billed at ~10% of the input rate — a big saving for RadAI, whose
            // ~5k-token app_knowledge system prompt is identical on every call.
            system = new object[]
            {
                new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } }
            },
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        var json = JsonSerializer.Serialize(payload);

        // Retry transient throttling (429) / overload (529) / availability blips
        // (503/500) with backoff (honouring a Retry-After header when present) so
        // a short burst doesn't surface as a hard failure mid-edit.
        const int maxAttempts = 3;
        string body = string.Empty;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, MessagesUrl);
            request.Headers.Add("x-api-key", _apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode) break;

            var status = (int)response.StatusCode;
            var transient = status is 429 or 529 or 503 or 500;
            if (!transient || attempt >= maxAttempts)
            {
                _logger.LogError("[Anthropic] {Status} (attempt {Attempt}/{Max}): {Body}", status, attempt, maxAttempts, body);
                throw new InvalidOperationException($"Anthropic API returned {status}.");
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
            _logger.LogWarning("[Anthropic] {Status} (attempt {Attempt}/{Max}) — retrying in {Delay}s", status, attempt, maxAttempts, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
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

        // Token usage for cost attribution. cache_read_input_tokens are the
        // system-prompt tokens served from the prompt cache (billed at ~10%).
        var usage = new AiUsage(0, 0, 0, 0);
        if (doc.RootElement.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            int Get(string n) => u.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            usage = new AiUsage(Get("input_tokens"), Get("output_tokens"), Get("cache_read_input_tokens"), Get("cache_creation_input_tokens"));
        }
        return (sb.ToString(), usage);
    }
}
