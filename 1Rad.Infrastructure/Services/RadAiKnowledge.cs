using System.Text;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Loads the RadAI help-desk knowledge pack from Resources/RadAI (copied beside
/// the app at build time) once on first use. Registered as a singleton. Tolerates
/// a missing pack: IsAvailable stays false so the handler degrades gracefully.
/// </summary>
public sealed class RadAiKnowledge : IRadAiKnowledge
{
    private readonly ILogger<RadAiKnowledge> _logger;
    private readonly object _gate = new();
    private bool _loaded;
    private bool _available;
    private string _systemPrompt = string.Empty;
    private string _knowledge = string.Empty;

    public RadAiKnowledge(ILogger<RadAiKnowledge> logger) => _logger = logger;

    public bool IsAvailable { get { EnsureLoaded(); return _available; } }

    public string BuildSystemPrompt(string? page)
    {
        EnsureLoaded();
        var sb = new StringBuilder();
        sb.Append(_systemPrompt);
        if (!string.IsNullOrWhiteSpace(page))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append($"The user is currently on the app screen/route '{page}'. Prefer answering in this screen's context when relevant.");
        }
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("=== APP KNOWLEDGE (answer ONLY from this) ===");
        sb.Append(_knowledge);
        return sb.ToString();
    }

    public object BuildResponseSchema() => new
    {
        type = "object",
        properties = new
        {
            answer = new { type = "string" },
            reply_language = new { type = "string", @enum = new[] { "en", "hi" } },
            suggested_followups = new { type = "array", items = new { type = "string" } },
            covered = new { type = "boolean" }
        },
        required = new[] { "answer", "reply_language", "suggested_followups", "covered" }
    };

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "Resources", "RadAI");
                _systemPrompt = File.ReadAllText(Path.Combine(dir, "assistant_system_prompt.md"));
                _knowledge = File.ReadAllText(Path.Combine(dir, "app_knowledge.json"));
                _available = _systemPrompt.Length > 0 && _knowledge.Length > 0;
                _logger.LogInformation("[RadAiKnowledge] loaded (available={Available}) from {Dir}", _available, dir);
            }
            catch (Exception ex)
            {
                _available = false;
                _logger.LogWarning(ex, "[RadAiKnowledge] failed to load the RadAI knowledge pack.");
            }
            finally { _loaded = true; }
        }
    }
}
