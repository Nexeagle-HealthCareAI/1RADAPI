using System.Linq;
using System.Text.Json.Nodes;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Loads the radiology knowledge pack from Resources/Radiology (copied beside the
/// app at build time) once on first use, and builds the per-call context block.
/// Registered as a singleton. Tolerates a missing / invalid pack: IsAvailable
/// stays false so the report formatter falls back to the generic polish action.
/// </summary>
public sealed class RadiologyPack : IRadiologyPack
{
    private readonly ILogger<RadiologyPack> _logger;
    private readonly object _gate = new();
    private bool _loaded;
    private bool _available;
    private string _systemPrompt = string.Empty;
    private JsonNode? _templates;
    private JsonNode? _lexicon;
    private JsonNode? _examples;

    public RadiologyPack(ILogger<RadiologyPack> logger) => _logger = logger;

    public bool IsAvailable { get { EnsureLoaded(); return _available; } }
    public string SystemPrompt { get { EnsureLoaded(); return _systemPrompt; } }

    public bool HasTest(string modality, string testCode)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(modality) || string.IsNullOrWhiteSpace(testCode))
            return false;
        return _templates?["modalities"]?[modality]?["tests"]?[testCode] is not null;
    }

    public string BuildContext(string modality, string testCode)
    {
        EnsureLoaded();
        var template = _templates?["modalities"]?[modality]?["tests"]?[testCode]
            ?? throw new InvalidOperationException($"No template for {modality}/{testCode} in report_templates.json");

        // First example whose test_code matches, else first for the same modality.
        JsonNode? example = null;
        if (_examples?["examples"] is JsonArray arr)
        {
            example = arr.FirstOrDefault(e => (string?)e?["test_code"] == testCode)
                   ?? arr.FirstOrDefault(e => (string?)e?["modality"] == modality);
        }

        return
            "TEMPLATE (use these sections, organ order, and normal-default lines):\n" +
            template.ToJsonString() + "\n\n" +
            "LEXICON (apply corrections; never alter protected_patterns; flag ambiguous_pairs):\n" +
            (_lexicon?.ToJsonString() ?? "{}") + "\n\n" +
            "EXAMPLE (match this house style and corrections/flags behaviour):\n" +
            (example?.ToJsonString() ?? "(none)");
    }

    /// <summary>
    /// Gemini's responseSchema accepts an OpenAPI subset (type/properties/required/
    /// items/enum). This mirrors output_schema.json in the Gemini-compatible form.
    /// </summary>
    public object BuildResponseSchema() => new
    {
        type = "object",
        properties = new
        {
            formatted_report = new { type = "string" },
            sections = new
            {
                type = "object",
                properties = new
                {
                    CLINICAL_HISTORY = new { type = "string" },
                    TECHNIQUE = new { type = "string" },
                    COMPARISON = new { type = "string" },
                    FINDINGS = new { type = "string" },
                    IMPRESSION = new { type = "string" }
                }
            },
            corrections = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        from = new { type = "string" },
                        to = new { type = "string" },
                        type = new { type = "string", @enum = new[] { "spelling", "grammar", "style", "abbreviation" } }
                    },
                    required = new[] { "from", "to", "type" }
                }
            },
            flags = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string" },
                        issue = new { type = "string" }
                    },
                    required = new[] { "text", "issue" }
                }
            },
            unchanged_protected = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "formatted_report", "sections", "corrections", "flags", "unchanged_protected" }
    };

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "Radiology");
                _systemPrompt = File.ReadAllText(Path.Combine(dir, "system_prompt.md"));
                _templates = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "report_templates.json")));
                _lexicon = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "radiology_lexicon.json")));
                _examples = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "few_shot_examples.json")));
                _available = !string.IsNullOrWhiteSpace(_systemPrompt) && _templates is not null;
                _logger.LogInformation("[RadiologyPack] loaded (available={Available}) from {Dir}", _available, dir);
            }
            catch (Exception ex)
            {
                _available = false;
                _logger.LogWarning(ex, "[RadiologyPack] pack failed to load; formatter falls back to polish.");
            }
            finally
            {
                _loaded = true;
            }
        }
    }
}
