namespace _1Rad.Application.Common;

/// <summary>
/// "How did the patient hear about us" - the ONE list of channels. Front desks used to type this
/// as free text ("Camp", "camp", "Health camp", "CAMPS"), so no report could total it. The web
/// now offers these as a dropdown; the API also accepts anything an older build or an import
/// sends, and reports read every stored value through <see cref="Classify"/> so existing
/// free-text data groups correctly too.
/// </summary>
public static class PatientSources
{
    public const string Other = "Other";
    /// <summary>Report bucket for patients whose channel was never recorded.</summary>
    public const string NotRecorded = "Not recorded";

    /// <summary>The dropdown, in display order. The first six are the labels the old quick-chips wrote, so existing rows are already canonical.</summary>
    public static readonly string[] Canonical =
    {
        "Friend / Family",
        "By Doctor",
        "Camp",
        "Social Media",
        "Previous Patient",
        "Walk-in",
        "Newspaper / Hoarding",
        "Online / Google",
        Other,
    };

    // Loose words -> channel, for values typed before the dropdown existed. Whole-word matches
    // only ("ad" must not match "advice" or "road").
    private static readonly (string Channel, string[] Words)[] Aliases =
    {
        ("Friend / Family",       new[] { "friend", "friends", "family", "relative", "relatives", "neighbour", "neighbor", "acquaintance", "word", "mouth" }),
        ("By Doctor",             new[] { "doctor", "doctors", "dr", "referral", "referred" }),
        ("Camp",                  new[] { "camp", "camps", "screening" }),
        ("Social Media",          new[] { "social", "facebook", "fb", "instagram", "insta", "whatsapp", "youtube", "twitter", "telegram", "reel" }),
        ("Previous Patient",      new[] { "previous", "old", "repeat", "returning", "existing", "earlier" }),
        ("Walk-in",               new[] { "walkin", "walk", "direct", "nearby", "passing", "signboard" }),
        ("Newspaper / Hoarding",  new[] { "newspaper", "paper", "hoarding", "banner", "pamphlet", "poster", "flyer", "ad", "ads", "advertisement", "advertising", "tv", "radio", "leaflet" }),
        ("Online / Google",       new[] { "google", "online", "website", "web", "internet", "search", "justdial", "maps" }),
    };

    private static string Key(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static readonly Dictionary<string, string> ByKey =
        Canonical.ToDictionary(Key, c => c);

    /// <summary>
    /// What to STORE. A value that is one of the channels (any casing / spacing) is written in its
    /// canonical spelling; anything else is kept exactly as typed (trimmed) - nothing a person
    /// entered is thrown away; reports bucket it under "Other".
    /// </summary>
    public static string Canonicalize(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;
        return ByKey.TryGetValue(Key(trimmed), out var canonical) ? canonical : trimmed;
    }

    /// <summary>
    /// What to REPORT under: a canonical channel, "Not recorded" for blank, or "Other". Understands
    /// the loose wording older rows carry (e.g. "Facebook" -> Social Media).
    /// </summary>
    public static string Classify(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0) return NotRecorded;
        if (ByKey.TryGetValue(Key(trimmed), out var canonical)) return canonical;

        var words = trimmed
            .Split(new[] { ' ', '/', ',', ';', '-', '_', '.', '&', '+', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();
        foreach (var (channel, keywords) in Aliases)
            if (keywords.Any(words.Contains)) return channel;

        return Other;
    }
}
