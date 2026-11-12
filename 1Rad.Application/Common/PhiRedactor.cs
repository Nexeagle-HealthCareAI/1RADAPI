using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace _1Rad.Application.Common;

/// <summary>
/// De-identifies report text before it is sent to a third-party AI provider:
/// each direct identifier is swapped for a stable placeholder, and
/// <see cref="Restore"/> puts the originals back in the model's response. So
/// no identifier reaches the provider, yet the radiologist sees real names.
///
/// Two layers:
///   1. The patient's own identifiers (full name AND each part of it, PTID,
///      visit number, phone, …) — matched as whole words only, so a short name
///      like "Ram" never eats into "frame".
///   2. Anything that LOOKS like a contact or ID number, for any person named
///      in the text: Indian mobile numbers, e-mail addresses, 12-digit Aadhaar
///      style numbers. Measurements ("2.3 cm", "12 x 14 mm") never match.
/// </summary>
public static class PhiRedactor
{
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    // +91 / 0 prefix optional, 10 digits starting 6-9, optional single space or dash after the 5th digit.
    private static readonly Regex Mobile = new(@"(?<![\w.])(?:\+?91[\s-]?|0)?[6-9]\d{4}[\s-]?\d{5}(?![\w.])", RegexOptions.Compiled);
    // Aadhaar style: 12 digits, optionally grouped 4-4-4.
    private static readonly Regex TwelveDigitId = new(@"(?<![\w.])\d{4}[\s-]?\d{4}[\s-]?\d{4}(?![\w.])", RegexOptions.Compiled);

    public static (string Text, Dictionary<string, string> Map) Redact(string? text, IEnumerable<string?> phiValues)
    {
        var map = new Dictionary<string, string>();
        var work = text ?? string.Empty;
        if (work.Length == 0) return (work, map);

        var i = 0;
        string Next() => $"[[PHI{i++}]]";

        // Longest first so "John Smith" masks before a stray "John".
        var values = (phiValues ?? Array.Empty<string?>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Where(v => v.Length >= 3)   // skip trivially short tokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length)
            .ToList();

        foreach (var raw in values)
        {
            // Whole words only: no letter/digit immediately before or after.
            var pattern = $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(raw)}(?![\p{{L}}\p{{N}}])";
            // Matching ignores case, but each distinct spelling gets its own placeholder, so Restore puts back
            // exactly what was written ("pooja" stays "pooja", "POOJA" stays "POOJA"), not the stored spelling.
            work = Regex.Replace(work, pattern, m =>
            {
                var existing = map.FirstOrDefault(kv => kv.Value == m.Value).Key;
                if (existing != null) return existing;
                var placeholder = Next();
                map[placeholder] = m.Value;
                return placeholder;
            }, RegexOptions.IgnoreCase);
        }

        foreach (var re in new[] { Email, Mobile, TwelveDigitId })
        {
            work = re.Replace(work, m =>
            {
                var existing = map.FirstOrDefault(kv => kv.Value == m.Value).Key;
                if (existing != null) return existing;
                var placeholder = Next();
                map[placeholder] = m.Value;
                return placeholder;
            });
        }
        return (work, map);
    }

    public static string Restore(string? text, Dictionary<string, string> map)
    {
        var work = text ?? string.Empty;
        if (work.Length == 0 || map.Count == 0) return work;
        foreach (var kv in map) work = work.Replace(kv.Key, kv.Value);
        return work;
    }

    /// <summary>
    /// The identifiers to mask for one person: the full name, each part of it
    /// (so "Mrs Kumari" is caught too — honorifics and initials skipped), and
    /// any other values given as-is.
    /// </summary>
    public static IEnumerable<string?> WithNameParts(string? fullName, params string?[] others)
    {
        var list = new List<string?> { fullName };
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            foreach (var part in Regex.Split(fullName, @"[\s.,^/]+"))
            {
                var p = part.Trim();
                if (p.Length >= 3 && !Honorifics.Contains(p)) list.Add(p);
            }
        }
        list.AddRange(others);
        return list;
    }

    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "miss", "mst", "smt", "shri", "sri", "baby", "master", "dr", "prof", "late",
    };
}
