using _1Rad.Application.Features.Reporting.Commands.GenerateVoiceReport;
using _1Rad.Application.Features.Reporting.Commands.AiAssist;
using _1Rad.Application.Features.Reporting.Commands.FormatReport;
using _1Rad.Application.Features.Reporting.Commands.DeleteKeyword;
using _1Rad.Application.Features.Reporting.Commands.DeleteTemplate;
using _1Rad.Application.Features.Reporting.Commands.SaveReport;
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
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace _1RadAPI.Controllers
{
    [Route("api/v1/[controller]")]
    [ApiController]
    [Authorize]
    public class ReportingController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IRadiologyCorpus _corpus;

        public ReportingController(IMediator mediator, IRadiologyCorpus corpus)
        {
            _mediator = mediator;
            _corpus = corpus;
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
