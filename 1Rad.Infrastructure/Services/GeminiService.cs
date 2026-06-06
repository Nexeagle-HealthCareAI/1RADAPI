using System.Text;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Calls Google Gemini (generativelanguage API) for the report editor's AI
/// co-pilot. Config read from the "Gemini" section (App Service config /
/// appsettings / env vars):
///   Gemini:ApiKey  — secret API key (required)
///   Gemini:Model   — model id (default "gemini-2.0-flash")
/// The key stays on the server — the browser only ever talks to our own API.
/// </summary>
public class GeminiService : IReportAiService
{
    private const string Base = "https://generativelanguage.googleapis.com/v1beta/models";
    private const string Placeholder = "YOUR_GEMINI_API_KEY";

    private readonly HttpClient _http;
    private readonly ILogger<GeminiService> _logger;
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiService(HttpClient http, IConfiguration configuration, ILogger<GeminiService> logger)
    {
        _http = http;
        _logger = logger;
        var section = configuration.GetSection("Gemini");
        // Trim — pipeline/library secrets often arrive with a trailing newline
        // or surrounding quotes, which makes Google reject the key.
        _apiKey = (section["ApiKey"] ?? string.Empty).Trim().Trim('"');
        _model = (section["Model"] ?? "gemini-2.0-flash").Trim();
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && _apiKey != Placeholder;

    public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        => GenerateInternalAsync(systemPrompt, userPrompt, null, null, cancellationToken);

    public Task<string> GenerateWithAudioAsync(string systemPrompt, string userText, byte[]? audio, string? audioMimeType, CancellationToken cancellationToken = default)
        => GenerateInternalAsync(systemPrompt, userText, audio, audioMimeType, cancellationToken);

    private async Task<string> GenerateInternalAsync(string systemPrompt, string userText, byte[]? audio, string? audioMimeType, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Gemini API key is not configured (set Gemini__ApiKey).");

        var hasAudio = audio is { Length: > 0 };
        _logger.LogInformation("[Gemini] using key: len={Len}, model={Model}, audio={Audio}", _apiKey.Length, _model, hasAudio);

        // Parts = user text, plus inline audio when present (Gemini transcribes it).
        var parts = new List<object> { new { text = userText } };
        if (hasAudio)
            parts.Add(new { inline_data = new { mime_type = string.IsNullOrWhiteSpace(audioMimeType) ? "audio/webm" : audioMimeType, data = Convert.ToBase64String(audio!) } });

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = parts.ToArray() } },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 4096 }
        };

        return await PostAsync(payload, cancellationToken);
    }

    public async Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, object? responseSchema, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Gemini API key is not configured (set Gemini__ApiKey).");

        // Structured output: force JSON, low temperature for deterministic
        // formatting, and (when supplied) constrain the shape with responseSchema.
        object generationConfig = responseSchema is null
            ? new { temperature = 0.1, maxOutputTokens = 8192, responseMimeType = "application/json" }
            : new { temperature = 0.1, maxOutputTokens = 8192, responseMimeType = "application/json", responseSchema };

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig
        };

        _logger.LogInformation("[Gemini] structured call: model={Model}, schema={HasSchema}", _model, responseSchema is not null);
        return await PostAsync(payload, cancellationToken);
    }

    public async Task<string> GenerateJsonAsync(string systemPrompt, string userPrompt, byte[]? audio, string? audioMimeType, object? responseSchema, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Gemini API key is not configured (set Gemini__ApiKey).");

        var parts = new List<object> { new { text = userPrompt } };
        var hasAudio = audio is { Length: > 0 };
        if (hasAudio)
            parts.Add(new { inline_data = new { mime_type = string.IsNullOrWhiteSpace(audioMimeType) ? "audio/webm" : audioMimeType, data = Convert.ToBase64String(audio!) } });

        object generationConfig = responseSchema is null
            ? new { temperature = 0.2, maxOutputTokens = 4096, responseMimeType = "application/json" }
            : new { temperature = 0.2, maxOutputTokens = 4096, responseMimeType = "application/json", responseSchema };

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = parts.ToArray() } },
            generationConfig
        };

        _logger.LogInformation("[Gemini] structured+audio call: model={Model}, audio={Audio}, schema={HasSchema}", _model, hasAudio, responseSchema is not null);
        return await PostAsync(payload, cancellationToken);
    }

    private async Task<string> PostAsync(object payload, CancellationToken cancellationToken)
    {
        // Key in the query string (Google's scheme). Never logged.
        var url = $"{Base}/{_model}:generateContent?key={_apiKey}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Gemini] {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Gemini API returned {(int)response.StatusCode}.");
        }

        // Response: { "candidates": [ { "content": { "parts": [ { "text": "..." } ] } } ] }
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var cand in candidates.EnumerateArray())
            {
                if (cand.TryGetProperty("content", out var content)
                    && content.TryGetProperty("parts", out var parts2) && parts2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts2.EnumerateArray())
                        if (part.TryGetProperty("text", out var text)) sb.Append(text.GetString());
                }
            }
        }
        return sb.ToString();
    }
}
