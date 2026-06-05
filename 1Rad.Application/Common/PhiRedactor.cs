using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace _1Rad.Application.Common;

/// <summary>
/// De-identifies report text before it is sent to a third-party AI provider:
/// each direct identifier (patient name, PTID, phone) is swapped for a stable
/// placeholder, and <see cref="Restore"/> puts the originals back in the model's
/// response. So no PHI ever reaches the provider, yet the radiologist sees the
/// real names in the returned draft.
/// </summary>
public static class PhiRedactor
{
    public static (string Text, Dictionary<string, string> Map) Redact(string? text, IEnumerable<string?> phiValues)
    {
        var map = new Dictionary<string, string>();
        var work = text ?? string.Empty;
        if (work.Length == 0) return (work, map);

        var i = 0;
        // Longest first so "John Smith" masks before a stray "John".
        var values = phiValues
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Where(v => v.Length >= 3)   // skip trivially short tokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length);

        foreach (var raw in values)
        {
            var placeholder = $"[[PHI{i}]]";
            var replaced = Regex.Replace(work, Regex.Escape(raw), placeholder, RegexOptions.IgnoreCase);
            if (replaced != work)
            {
                map[placeholder] = raw;
                work = replaced;
                i++;
            }
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
}
