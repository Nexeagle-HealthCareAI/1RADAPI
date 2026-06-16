using System.Text.Json;
using System.Text.RegularExpressions;
using _1Rad.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Pure-.NET grammar/style checker (Option C). No Java, no IKVM, no server, no
/// LLM, no extra NuGet packages — runs in-process with the regex engine. Returns
/// the LanguageTool JSON shape ({ matches: [...] }) the editor already parses.
///
/// Spelling stays the editor's live client-side check (red squiggles); this
/// service focuses on high-precision grammar/typography rules that are SAFE for
/// telegraphic radiology report style (it deliberately does NOT flag sentence
/// fragments, capitalization, a/an, or anything context-dependent). The rule set
/// is intended to grow over time; add a Rule entry and it shows up immediately.
/// </summary>
public class RadiologyGrammarService : ILanguageToolService
{
    private readonly ILogger<RadiologyGrammarService> _logger;

    public RadiologyGrammarService(ILogger<RadiologyGrammarService> logger) => _logger = logger;

    public bool IsConfigured => true;   // always available — it's in-process

    // High-precision, low-false-positive rules. Offsets are computed against the
    // exact plain text the editor sent, so the editor's offset map lines up.
    private static readonly Regex RepeatedWord =
        new(@"\b([A-Za-z]{2,})\s+\1\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DoubleSpace =
        new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunct =
        new(@"[ \t]+([,;:!?])", RegexOptions.Compiled);
    private static readonly Regex MissingSpaceAfterPunct =      // not before digits (protects 2.3, 2:1)
        new(@"([,;:])([A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex SpaceInsideOpenParen =
        new(@"\([ \t]+", RegexOptions.Compiled);
    private static readonly Regex SpaceInsideCloseParen =
        new(@"[ \t]+\)", RegexOptions.Compiled);
    private static readonly Regex RepeatedPunct =
        new(@"([,;:!?])\1+", RegexOptions.Compiled);

    public Task<string> CheckAsync(string text, string language, CancellationToken cancellationToken = default)
        => Task.FromResult(Check(text ?? string.Empty));

    private string Check(string text)
    {
        var found = new List<(int offset, int length, string message, string repl, string issueType)>();

        foreach (Match m in RepeatedWord.Matches(text))
            found.Add((m.Index, m.Length, "Possible repeated word.", m.Groups[1].Value, "grammar"));
        foreach (Match m in DoubleSpace.Matches(text))
            found.Add((m.Index, m.Length, "Multiple consecutive spaces.", " ", "typographical"));
        foreach (Match m in SpaceBeforePunct.Matches(text))
            found.Add((m.Index, m.Length, "Remove the space before the punctuation.", m.Groups[1].Value, "typographical"));
        foreach (Match m in MissingSpaceAfterPunct.Matches(text))
            found.Add((m.Index, m.Length, "Add a space after the punctuation.", m.Groups[1].Value + " " + m.Groups[2].Value, "typographical"));
        foreach (Match m in SpaceInsideOpenParen.Matches(text))
            found.Add((m.Index, m.Length, "Remove the space inside the parenthesis.", "(", "typographical"));
        foreach (Match m in SpaceInsideCloseParen.Matches(text))
            found.Add((m.Index, m.Length, "Remove the space inside the parenthesis.", ")", "typographical"));
        foreach (Match m in RepeatedPunct.Matches(text))
            found.Add((m.Index, m.Length, "Repeated punctuation.", m.Groups[1].Value, "typographical"));

        var matches = found
            .OrderBy(r => r.offset)
            .Take(300)
            .Select(r => new
            {
                offset = r.offset,
                length = r.length,
                message = r.message,
                replacements = string.IsNullOrEmpty(r.repl) ? Array.Empty<object>() : new object[] { new { value = r.repl } },
                rule = new { issueType = r.issueType, id = "RAD_" + r.issueType.ToUpperInvariant() }
            });

        return JsonSerializer.Serialize(new { matches });
    }
}
