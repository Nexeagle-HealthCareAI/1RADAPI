using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;

namespace _1Rad.Application.Features.Assist.RadAi;

public record RadAiTurn(string Role, string Text); // role: "user" | "assistant"

/// <summary>
/// RadAI — the in-app help desk. Answers staff questions about how to USE the
/// 1Rad app, grounded ONLY on the app knowledge base, in the user's language
/// (Hindi or English). Accepts a typed question OR spoken audio (Gemini
/// transcribes + answers in one call). Returns a structured reply (answer +
/// reply language + suggested follow-ups + a 'covered' honesty flag). Backend-only:
/// the API key never reaches the browser, and no patient data is sent.
/// </summary>
public record RadAiCommand : IRequest<RadAiResult>
{
    public string? Question { get; init; }
    public string? Page { get; init; }
    public string? Lang { get; init; }              // "en" | "hi" hint (optional)
    public string? AudioBase64 { get; init; }
    public string? AudioMimeType { get; init; }
    public List<RadAiTurn>? History { get; init; }
}

public record RadAiResult(
    bool Success,
    string? Answer,
    string? ReplyLanguage,                            // "en" | "hi" — drives voice-out
    List<string>? SuggestedFollowups,
    bool Covered,                                     // false => the KB didn't cover it
    string? Error);

public class RadAiCommandHandler : IRequestHandler<RadAiCommand, RadAiResult>
{
    // Typed questions run on Claude Haiku; spoken questions need Gemini's inline
    // audio transcription (Claude's API can't take audio).
    private readonly IReportAiService _gemini;
    private readonly IAnthropicService _claude;
    private readonly IRadAiKnowledge _knowledge;
    private readonly IApplicationDbContext _db;
    private readonly IRadAiResponseCache _cache;

    // Response-cache lifetime. Long enough to absorb bursts of the same question,
    // short enough that a knowledge-pack change takes effect quickly (the cache
    // key also hashes the system prompt, so a content change invalidates anyway).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);

    public RadAiCommandHandler(IReportAiService gemini, IAnthropicService claude, IRadAiKnowledge knowledge, IApplicationDbContext db, IRadAiResponseCache cache)
    {
        _gemini = gemini;
        _claude = claude;
        _knowledge = knowledge;
        _db = db;
        _cache = cache;
    }

    public async Task<RadAiResult> Handle(RadAiCommand request, CancellationToken cancellationToken)
    {
        var hasAudio = !string.IsNullOrWhiteSpace(request.AudioBase64);
        if (!hasAudio && string.IsNullOrWhiteSpace(request.Question))
            return Fail("Ask me something — type or use the mic.");

        // Each path needs its own provider configured (audio → Gemini, text → Claude).
        var aiReady = hasAudio ? _gemini.IsConfigured : _claude.IsConfigured;
        if (!aiReady || !_knowledge.IsAvailable)
            return Fail("RadAI isn't switched on yet. Please contact your admin.");

        var system = _knowledge.BuildSystemPrompt(request.Page);

        var user = new StringBuilder();
        user.AppendLine($"User language hint: {(string.IsNullOrWhiteSpace(request.Lang) ? "auto" : request.Lang)}");
        if (request.History is { Count: > 0 })
        {
            user.AppendLine("=== CONVERSATION SO FAR ===");
            foreach (var t in request.History.TakeLast(8))
                user.AppendLine($"{(t.Role == "assistant" ? "RadAI" : "User")}: {t.Text}");
            user.AppendLine();
        }
        user.AppendLine(hasAudio
            ? "The user's question is in the attached audio (Hindi or English). Understand it, then answer."
            : "User question: " + request.Question!.Trim());

        var schema = _knowledge.BuildResponseSchema();
        var langHint = string.IsNullOrWhiteSpace(request.Lang) ? "auto" : request.Lang!.Trim().ToLowerInvariant();

        // Response cache (typed questions only — voice has no stable text key).
        // A hit skips the model entirely, so 100% of the tokens that call would
        // have billed are saved; we log what was avoided so it can be measured.
        // The key hashes the system prompt, so a knowledge-pack change auto-
        // invalidates stale answers.
        string? cacheKey = null;
        if (!hasAudio && !string.IsNullOrWhiteSpace(request.Question))
        {
            cacheKey = BuildCacheKey(request.Question!, request.Page, langHint, system);
            if (_cache.TryGet(cacheKey, out var hit) && hit is not null)
            {
                await LogUsageAsync(request, wasVoice: false, model: "cache", answer: hit.Answer,
                    lang: hit.ReplyLanguage, covered: hit.Covered,
                    inputTokens: 0, outputTokens: 0, cacheReadInput: 0, cacheHit: true,
                    savedInput: hit.SavedInputTokens, savedOutput: hit.SavedOutputTokens, ct: cancellationToken);
                return new RadAiResult(true, hit.Answer, hit.ReplyLanguage, new List<string>(), hit.Covered, null);
            }
        }

        string json;
        AiUsage usage;
        try
        {
            if (hasAudio)
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(StripDataUrl(request.AudioBase64!)); }
                catch { return Fail("Couldn't read the audio. Please try again or type your question."); }
                // Spoken → Gemini (inline audio transcription + answer in one call).
                json = await _gemini.GenerateJsonAsync(system, user.ToString(), bytes, request.AudioMimeType, schema, cancellationToken);
                usage = EstimateUsage(system, user.ToString(), json); // Gemini gives no usage on this path — estimate.
            }
            else
            {
                // Typed → Claude Haiku (prompt caching + real token usage).
                var res = await _claude.GenerateJsonWithUsageAsync(system, user.ToString(), schema, cancellationToken);
                json = res.Text;
                usage = res.Usage;
            }
        }
        catch
        {
            // Never block the user on a third-party API — friendly fallback.
            return Fail("I'm having trouble right now. Please try again in a moment.");
        }

        RadAiAnswer? parsed;
        try { parsed = JsonSerializer.Deserialize<RadAiAnswer>(StripFence(json), JsonOpts); }
        catch { parsed = null; }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Answer))
            return Fail("I didn't catch that — could you rephrase?");

        var lang = parsed.ReplyLanguage == "hi" ? "hi" : "en";
        var followups = (parsed.SuggestedFollowups ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList();

        // Cache the answer (typed path) so the next identical question is free.
        // Store what THIS call billed, so a future hit can credit the saving.
        if (cacheKey is not null)
            _cache.Set(cacheKey, new RadAiCachedAnswer(parsed.Answer.Trim(), lang, parsed.Covered, usage.InputTokens, usage.OutputTokens), CacheTtl);

        // Capture usage + the question for the retrain + token-savings dashboards.
        // Best-effort: it must never break or delay the user's answer.
        await LogUsageAsync(request, wasVoice: hasAudio, model: hasAudio ? "gemini" : "claude",
            answer: parsed.Answer, lang: lang, covered: parsed.Covered,
            inputTokens: usage.InputTokens, outputTokens: usage.OutputTokens, cacheReadInput: usage.CacheReadInputTokens,
            cacheHit: false, savedInput: 0, savedOutput: 0, ct: cancellationToken);

        return new RadAiResult(true, parsed.Answer.Trim(), lang, followups, parsed.Covered, null);
    }

    private async Task LogUsageAsync(RadAiCommand req, bool wasVoice, string model, string? answer, string lang, bool covered,
        int inputTokens, int outputTokens, int cacheReadInput, bool cacheHit, int savedInput, int savedOutput, CancellationToken ct)
    {
        try
        {
            var uc = _db.UserContext;
            var snippet = string.IsNullOrWhiteSpace(answer)
                ? null
                : (answer.Length > 480 ? answer[..480] : answer);

            _db.RadAiQuestionLogs.Add(new RadAiQuestionLog
            {
                HospitalId           = uc.HospitalId,
                AskedByUserId        = uc.UserId == Guid.Empty ? (Guid?)null : uc.UserId,
                SessionId            = uc.SessionId,
                Question             = wasVoice ? null : req.Question?.Trim(),
                WasVoice             = wasVoice,
                Page                 = string.IsNullOrWhiteSpace(req.Page) ? null : req.Page!.Trim(),
                ReplyLanguage        = lang,
                Covered              = covered,
                AnswerSnippet        = snippet,
                Model                = model,
                InputTokens          = inputTokens,
                OutputTokens         = outputTokens,
                CacheReadInputTokens = cacheReadInput,
                CacheHit             = cacheHit,
                SavedInputTokens     = savedInput,
                SavedOutputTokens    = savedOutput,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Logging is best-effort; never surface a failure to the user.
        }
    }

    // Stable cache key: normalised question + page + language + a short hash of
    // the system prompt (which encodes the knowledge-pack version), so the cache
    // is shared across centres (RadAI answers are generic, non-PHI help) and
    // invalidates when app_knowledge.json changes.
    private static string BuildCacheKey(string question, string? page, string langHint, string systemPrompt)
    {
        var norm = Regex.Replace(question.ToLowerInvariant(), "[^a-z0-9 ]", " ");
        norm = Regex.Replace(norm, "\\s+", " ").Trim();
        return $"radai:{ShortHash(systemPrompt)}:{page}:{langHint}:{norm}";
    }

    private static string ShortHash(string s)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 6); // 12 hex chars — plenty to version the prompt
    }

    // ~4 chars/token heuristic for the Gemini (audio) path, which doesn't return
    // usage here. Claude returns real counts via GenerateJsonWithUsageAsync.
    private static AiUsage EstimateUsage(string system, string user, string output)
    {
        static int Est(string t) => string.IsNullOrEmpty(t) ? 0 : (t.Length + 3) / 4;
        return new AiUsage(Est(system) + Est(user), Est(output), 0, 0);
    }

    private static RadAiResult Fail(string msg) => new(false, null, null, null, false, msg);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // The model's structured reply (matches assistant_system_prompt.md output).
    private sealed class RadAiAnswer
    {
        [JsonPropertyName("answer")] public string Answer { get; set; } = "";
        [JsonPropertyName("reply_language")] public string ReplyLanguage { get; set; } = "en";
        [JsonPropertyName("suggested_followups")] public List<string>? SuggestedFollowups { get; set; }
        [JsonPropertyName("covered")] public bool Covered { get; set; } = true;
    }

    private static string StripDataUrl(string b64)
    {
        var idx = b64.IndexOf(',');
        return (b64.StartsWith("data:") && idx >= 0) ? b64[(idx + 1)..] : b64;
    }

    // Strip a ```json fence the model may wrap the JSON in (belt-and-braces;
    // responseMimeType=application/json normally prevents it).
    private static string StripFence(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "{}";
        var t = s.Trim();
        var m = Regex.Match(t, "^```(?:[a-zA-Z]+)?\\s*\\n([\\s\\S]*?)\\n```\\s*$");
        return m.Success ? m.Groups[1].Value.Trim() : t;
    }
}
