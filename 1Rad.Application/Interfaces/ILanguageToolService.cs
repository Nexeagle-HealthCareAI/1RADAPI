namespace _1Rad.Application.Interfaces;

/// <summary>
/// Grammar/style checking via a SELF-HOSTED LanguageTool server, so report text
/// (which may contain PHI) never leaves the organisation's network. Configured
/// from "LanguageTool:BaseUrl" (e.g. http://languagetool:8010). When the base
/// URL is unset the feature is treated as disabled (IsConfigured == false).
/// </summary>
public interface ILanguageToolService
{
    /// <summary>True when a LanguageTool base URL is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// POSTs the text to {BaseUrl}/v2/check and returns the LanguageTool JSON
    /// response verbatim (the { matches: [...] } shape the editor expects).
    /// </summary>
    Task<string> CheckAsync(string text, string language, CancellationToken cancellationToken = default);
}
