using _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;
using _1Rad.Application.Features.Reporting.Commands.AiAssist;
using _1Rad.Application.Features.Reporting.Commands.FormatReport;
using _1Rad.Application.Features.Reporting.Commands.DeleteKeyword;
using _1Rad.Application.Features.Reporting.Commands.DeleteTemplate;
using _1Rad.Application.Features.Reporting.Commands.SaveReport;
using _1Rad.Application.Features.Reporting.Commands.FinalizeReport;
using _1Rad.Application.Features.Reporting.Commands.AddAddendum;
using _1Rad.Application.Features.Reporting.Commands.UpsertKeyword;
using _1Rad.Application.Features.Reporting.Commands.UpsertTemplate;
using _1Rad.Application.Features.Reporting.Queries.GetKeywords;
using _1Rad.Application.Features.Reporting.Queries.GetReport;
using _1Rad.Application.Features.Reporting.Queries.GetReportsDelta;
using _1Rad.Application.Features.Reporting.Queries.BuildTermFrequency;
using _1Rad.Application.Common.Exceptions;
using _1Rad.Application.Features.Reporting.Queries.GetTemplates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    [Authorize]
    public class ReportingController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IRadiologyCorpus _corpus;
        private readonly ILanguageToolService _languageTool;
        private readonly IAnthropicService _anthropic;
        private readonly IConfiguration _config;

        public ReportingController(IMediator mediator, IRadiologyCorpus corpus, ILanguageToolService languageTool, IAnthropicService anthropic, IConfiguration config)
        {
            _mediator = mediator;
            _corpus = corpus;
            _languageTool = languageTool;
            _anthropic = anthropic;
            _config = config;
        }

        public record GrammarCheckRequest(string Text, string? Language);

        /// <summary>
        /// Grammar/style check. Report text (potentially PHI) stays on-network —
        /// this replaces the editor's old direct call to public api.languagetool.org.
        /// Tiered, no extra infrastructure required:
        ///   1. a self-hosted LanguageTool server, if LanguageTool:BaseUrl is set
        ///      (deterministic, best quality); otherwise
        ///   2. the LLM proofreader (existing Anthropic service) — medical-aware and
        ///      needs no Docker/Java/extra server.
        /// Always returns the LanguageTool JSON shape ({ matches: [...] }) so the
        /// editor's existing parser is unchanged.
        /// </summary>
        [HttpPost("grammar-check")]
        public async Task<IActionResult> GrammarCheck([FromBody] GrammarCheckRequest body, CancellationToken cancellationToken)
        {
            var text = body?.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return Content("{\"matches\":[]}", "application/json");

            // Safety cap — a single report is far smaller; protects upstream.
            if (text.Length > 60000) text = text.Substring(0, 60000);
            var language = string.IsNullOrWhiteSpace(body?.Language) ? "en-US" : body!.Language!.Trim();

            try
            {
                if (_languageTool.IsConfigured)
                {
                    var json = await _languageTool.CheckAsync(text, language, cancellationToken);
                    return Content(json, "application/json");
                }
                // LLM fallback is opt-out: set LanguageTool:GrammarLlmFallback=false to
                // guarantee grammar never uses the LLM (e.g. when relying solely on the
                // embedded LanguageTool engine).
                var allowLlm = _config.GetValue<bool?>("LanguageTool:GrammarLlmFallback") ?? true;
                if (allowLlm && _anthropic.IsConfigured)
                {
                    var json = await BuildLlmGrammarMatchesAsync(text, cancellationToken);
                    return Content(json, "application/json");
                }
                return StatusCode(503, new { success = false, code = "GRAMMAR_DISABLED", error = "Grammar service is not configured on this server." });
            }
            catch (Exception ex)
            {
                return StatusCode(502, new { success = false, code = "GRAMMAR_UPSTREAM", error = ex.Message });
            }
        }

        // LLM proofreader fallback: ask Claude for genuine spelling/grammar errors
        // (NOT radiology style), then map each flagged substring back to a character
        // offset in the report text and emit LanguageTool-shaped matches. Offsets are
        // computed against the SAME plain text the editor sent, so the editor's
        // offset map lines up.
        private async Task<string> BuildLlmGrammarMatchesAsync(string text, CancellationToken cancellationToken)
        {
            const string sys =
                "You are a meticulous proofreader for radiology reports. Identify ONLY genuine spelling " +
                "and grammar errors. DO NOT flag: medical/radiology terminology, drug names, eponyms, " +
                "abbreviations (e.g. MRI, FLAIR, T2), measurements (e.g. 2.3 cm), or the telegraphic/" +
                "elliptical style typical of radiology reports (sentence fragments like 'No acute " +
                "abnormality.' are correct). Be conservative — when in doubt, do not flag.";
            var user =
                "Report text:\n\"\"\"\n" + text + "\n\"\"\"\n\n" +
                "Return a SINGLE JSON object: {\"issues\":[{\"text\":\"<exact minimal substring copied " +
                "verbatim from the report that contains the error>\",\"suggestion\":\"<the corrected " +
                "substring>\",\"message\":\"<short reason>\",\"type\":\"spelling\"|\"grammar\"}]}. " +
                "If there are no errors return {\"issues\":[]}.";

            var raw = await _anthropic.GenerateJsonAsync(sys, user, null, cancellationToken);
            var matches = new List<object>();
            try
            {
                var json = StripJsonFences(raw);
                using var docJson = JsonDocument.Parse(json);
                var root = docJson.RootElement;
                JsonElement issues;
                if (root.ValueKind == JsonValueKind.Array) issues = root;
                else if (!root.TryGetProperty("issues", out issues) || issues.ValueKind != JsonValueKind.Array)
                    return "{\"matches\":[]}";

                int searchFrom = 0;
                foreach (var issue in issues.EnumerateArray())
                {
                    var bad = issue.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                    if (string.IsNullOrEmpty(bad)) continue;
                    var suggestion = issue.TryGetProperty("suggestion", out var sEl) ? (sEl.GetString() ?? "") : "";
                    var message = issue.TryGetProperty("message", out var mEl) ? (mEl.GetString() ?? "") : "";
                    var type = issue.TryGetProperty("type", out var tyEl) ? (tyEl.GetString() ?? "grammar") : "grammar";

                    int idx = text.IndexOf(bad, Math.Min(searchFrom, text.Length), StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) idx = text.IndexOf(bad, 0, StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) continue;             // model echoed text we can't locate — skip
                    searchFrom = idx + bad.Length;

                    matches.Add(new
                    {
                        offset = idx,
                        length = bad.Length,
                        message,
                        replacements = string.IsNullOrEmpty(suggestion) ? new object[0] : new object[] { new { value = suggestion } },
                        rule = new { issueType = type == "spelling" ? "misspelling" : "grammar", id = "AI_GRAMMAR" }
                    });
                }
            }
            catch
            {
                return "{\"matches\":[]}";          // unpar.seable model output → no issues, not an error
            }
            return JsonSerializer.Serialize(new { matches });
        }

        // Strip ```json … ``` fences the model sometimes wraps JSON in.
        private static string StripJsonFences(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "{}";
            var t = s.Trim();
            if (t.StartsWith("```"))
            {
                int nl = t.IndexOf('\n');
                if (nl >= 0) t = t.Substring(nl + 1);
                if (t.EndsWith("```")) t = t.Substring(0, t.Length - 3);
                t = t.Trim();
            }
            return t;
        }

        // --- TEMPLATE QUERIES & COMMANDS ---
        
        /// <summary>
        /// Get report templates for the current hospital and doctor
        /// </summary>
        [HttpGet("templates")]
        public async Task<IActionResult> GetTemplates([FromQuery] string? modality = null)
        {
            try
            {
                var query = new GetTemplatesQuery { Modality = modality };
                var templates = await _mediator.Send(query);
                return Ok(new { success = true, data = templates });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve templates: {ex.Message}" });
            }
        }

        /// <summary>
        /// Create or update a report template
        /// </summary>
        [HttpPost("templates/upsert")]
        public async Task<IActionResult> UpsertTemplate([FromBody] UpsertTemplateCommand command)
        {
            try
            {
                var template = await _mediator.Send(command);
                return Ok(new { success = true, data = template });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save template: {ex.Message}" });
            }
        }

        /// <summary>
        /// Delete a report template
        /// </summary>
        [HttpDelete("templates/{id}")]
        public async Task<IActionResult> DeleteTemplate(Guid id)
        {
            try
            {
                var command = new DeleteTemplateCommand { Id = id };
                await _mediator.Send(command);
                return Ok(new { success = true });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to delete template: {ex.Message}" });
            }
        }

        // --- KEYWORD INTELLIGENCE ---
        
        /// <summary>
        /// Get reporting keywords for the current doctor
        /// </summary>
        [HttpGet("keywords")]
        public async Task<IActionResult> GetKeywords()
        {
            try
            {
                var query = new GetKeywordsQuery();
                var keywords = await _mediator.Send(query);
                return Ok(new { success = true, data = keywords });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve keywords: {ex.Message}" });
            }
        }

        /// <summary>
        /// Create or update a reporting keyword
        /// </summary>
        [HttpPost("keywords/upsert")]
        public async Task<IActionResult> UpsertKeyword([FromBody] UpsertKeywordCommand command)
        {
            try
            {
                var keyword = await _mediator.Send(command);
                return Ok(new { success = true, data = keyword });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save keyword: {ex.Message}" });
            }
        }

        /// <summary>
        /// Delete a reporting keyword
        /// </summary>
        [HttpDelete("keywords/{id}")]
        public async Task<IActionResult> DeleteKeyword(Guid id)
        {
            try
            {
                var command = new DeleteKeywordCommand { Id = id };
                await _mediator.Send(command);
                return Ok(new { success = true });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to delete keyword: {ex.Message}" });
            }
        }

        // --- REPORT PERSISTENCE ---
        
        /// <summary>
        /// Get diagnostic report by appointment ID. Pass ?serviceId= to
        /// scope the lookup to a specific AppointmentService line — the
        /// multi-service rollout path. When omitted the endpoint
        /// behaves as it always has (single-report-per-appointment).
        /// </summary>
        [HttpGet("report/{appointmentId}")]
        public async Task<IActionResult> GetReport(string appointmentId, [FromQuery] Guid? serviceId = null)
        {
            try
            {
                var query = new GetReportQuery { AppointmentId = appointmentId, AppointmentServiceId = serviceId };
                var report = await _mediator.Send(query);
                return Ok(new { success = true, data = report });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Cloud PACS-only: fetch the report written directly against an
        /// ImagingStudy (no appointment).
        /// </summary>
        [HttpGet("report/by-study/{studyId:guid}")]
        public async Task<IActionResult> GetReportByStudy(Guid studyId)
        {
            try
            {
                var report = await _mediator.Send(new GetReportQuery { ImagingStudyId = studyId });
                return Ok(new { success = true, data = report });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to retrieve report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Bulk delta-pull of diagnostic reports for the Phase B1 offline cache.
        /// Returns a flat list (no nested fields) filtered by UpdatedAt > updatedAfter.
        /// Pass includeDeleted=true to receive tombstones.
        /// </summary>
        [HttpGet("reports")]
        public async Task<IActionResult> GetReportsDelta(
            [FromQuery] DateTime? updatedAfter,
            [FromQuery] bool includeDeleted = false)
        {
            var result = await _mediator.Send(new GetReportsDeltaQuery(updatedAfter, includeDeleted));
            return Ok(result);
        }

        /// <summary>
        /// Save or update a diagnostic report
        /// </summary>
        [HttpPost("save")]
        public async Task<IActionResult> SaveReport([FromBody] SaveReportCommand command)
        {
            try
            {
                var report = await _mediator.Send(command);
                return Ok(new { success = true, data = report });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (ReportLockedException ex)
            {
                // The report is signed-Final; its content is immutable. The
                // frontend uses REPORT_LOCKED to stop autosave and prompt the
                // radiologist to add an addendum instead of editing.
                return Conflict(new { success = false, code = "REPORT_LOCKED", error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (OccConflictException ex)
            {
                // B2 Track 3 — auto-merge + Undo. Body carries the server's
                // current state so the frontend can overwrite the editor
                // and offer the radiologist a 30s window to re-apply
                // their version if they want to win the race deliberately.
                return Conflict(new
                {
                    success = false,
                    code = "OCC_CONFLICT",
                    error = ex.Message,
                    data = ex.Server,
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to save report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Electronically sign a report (21 CFR Part 11). Re-authenticates the
        /// radiologist with their password, binds the signature to their
        /// identity, hashes + (for Final) locks the content, and appends a
        /// tamper-evident audit event. TargetStatus = "Preliminary" | "Final".
        /// </summary>
        [HttpPost("report/finalize")]
        public async Task<IActionResult> FinalizeReport([FromBody] FinalizeReportCommand command)
        {
            try
            {
                var report = await _mediator.Send(command);
                return Ok(new { success = true, data = report });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (ReportLockedException ex)
            {
                return Conflict(new { success = false, code = "REPORT_LOCKED", error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                // Wrong password (signature not applied) or not the report owner.
                return StatusCode(403, new { success = false, code = "SIGN_REAUTH_FAILED", error = ex.Message });
            }
            catch (OccConflictException ex)
            {
                return Conflict(new { success = false, code = "OCC_CONFLICT", error = ex.Message, data = ex.Server });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to finalize report: {ex.Message}" });
            }
        }

        /// <summary>
        /// Append a formal addendum to a finalised report (21 CFR Part 11). The
        /// signed content is never altered — the addendum is its own immutable,
        /// password-reauthenticated record and the report advances to "Addended".
        /// </summary>
        [HttpPost("report/addendum")]
        public async Task<IActionResult> AddAddendum([FromBody] AddAddendumCommand command)
        {
            try
            {
                var report = await _mediator.Send(command);
                return Ok(new { success = true, data = report });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { success = false, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, code = "SIGN_REAUTH_FAILED", error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Failed to add addendum: {ex.Message}" });
            }
        }

        /// <summary>
        /// Voice Reporting — turn a dictation transcript into a structured
        /// report draft using Claude Haiku. Returns editor-ready HTML.
        /// </summary>
        [HttpPost("voice-generate")]
        public async Task<IActionResult> GenerateVoiceReport([FromBody] GenerateVoiceReportCommand command)
        {
            try
            {
                var result = await _mediator.Send(command);
                if (!result.Success)
                    return BadRequest(new { success = false, error = result.Error ?? "Generation failed." });
                return Ok(new { success = true, html = result.Html });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Voice report generation failed: {ex.Message}" });
            }
        }

        /// <summary>
        /// Inline AI co-pilot — improve / proofread / expand / shorten a
        /// selection, or generate an impression from the findings. Returns
        /// editor-ready HTML.
        /// </summary>
        [HttpPost("ai-assist")]
        public async Task<IActionResult> AiAssist([FromBody] AiAssistCommand command)
        {
            try
            {
                var result = await _mediator.Send(command);
                if (!result.Success)
                    return BadRequest(new { success = false, error = result.Error ?? "AI request failed." });
                return Ok(new { success = true, html = result.Html });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"AI request failed: {ex.Message}" });
            }
        }

        // ── RadLex term services (autocomplete + spell-check) ──────────────────

        /// <summary>Autocomplete: radiology terms starting with the prefix.</summary>
        [HttpGet("terms/suggest")]
        public IActionResult SuggestTerms([FromQuery] string? q, [FromQuery] int limit = 8)
        {
            var items = _corpus.Suggest(q ?? string.Empty, Math.Clamp(limit, 1, 20));
            return Ok(new { success = true, items });
        }

        public sealed record SpellCheckBody(string? Text);
        public sealed record SpellIssue(string Word, List<string> Suggestions);

        /// <summary>
        /// Spell-check: words that are NOT a known radiology term AND have a likely
        /// fix (a known correction or a near term). Only fixable words are flagged,
        /// so plain English words — which have no close radiology term — aren't.
        /// </summary>
        [HttpPost("terms/check")]
        public IActionResult CheckSpelling([FromBody] SpellCheckBody body)
        {
            var text = body?.Text ?? string.Empty;
            var issues = new List<SpellIssue>();
            if (_corpus.IsAvailable && text.Length > 0)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(text, "[A-Za-z][A-Za-z-]{2,}"))
                {
                    var word = m.Value;
                    if (!seen.Add(word.ToLowerInvariant())) continue;          // unique words
                    if (string.Equals(word, "PHI", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_corpus.IsProtected(word) || _corpus.IsTerm(word)) continue;

                    var sugg = new List<string>();
                    var known = _corpus.Correction(word);
                    if (known != null) sugg.Add(known);
                    var near = _corpus.NearestTerm(word, 2);
                    if (near != null && !sugg.Contains(near, StringComparer.OrdinalIgnoreCase)) sugg.Add(near);

                    if (sugg.Count > 0) issues.Add(new SpellIssue(word, sugg));  // only flag fixable typos
                }
            }
            return Ok(new { success = true, issues });
        }

        /// <summary>
        /// Term-usage frequency mined from THIS clinic's diagnostic reports, so the
        /// editor autocomplete can rank the 68k RadLex corpus by real usage instead
        /// of a heuristic. Returns a term→count map (ordered most-used first).
        ///
        /// Workflow: call this once → save the response's `frequency` object to
        /// easyrad/public/data/radlex_frequency.json → re-run the corpus generator
        /// (node scripts/build-radlex-corpus.mjs) to fold real usage into the ranking.
        /// </summary>
        [HttpGet("terms/frequency")]
        public async Task<IActionResult> TermFrequency([FromQuery] int maxNgram = 5)
        {
            var freq = await _mediator.Send(new BuildTermFrequencyQuery(maxNgram));
            // Order desc so the most-used terms are easy to eyeball at the top of
            // the saved JSON (the generator reads it as an unordered map regardless).
            var ordered = freq
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Ok(new { success = true, count = ordered.Count, frequency = ordered });
        }

        /// <summary>
        /// RadAI report formatter — structured cleanup for a known modality/test
        /// using the knowledge pack. Returns the formatted report as editor HTML
        /// plus corrections + flags + preserved items for the review UI. Falls back
        /// (success=false) when the study has no template or the AI is unavailable.
        /// </summary>
        [HttpPost("format")]
        public async Task<IActionResult> Format([FromBody] FormatReportCommand command)
        {
            try
            {
                var result = await _mediator.Send(command);
                if (!result.Success)
                    return BadRequest(new { success = false, error = result.Error ?? "Formatting failed." });
                return Ok(new
                {
                    success = true,
                    html = result.Html,
                    data = new
                    {
                        formattedText = result.FormattedText,
                        corrections = result.Corrections,
                        flags = result.Flags,
                        unchangedProtected = result.UnchangedProtected,
                    },
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, error = $"Formatting failed: {ex.Message}" });
            }
        }
    }
}
