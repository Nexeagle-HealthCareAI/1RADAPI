using System.Globalization;

namespace _1Rad.Infrastructure.Interop;

/// <summary>Parsing for the FHIR search parameters the facade supports.</summary>
public static class FhirSearch
{
    public const int DefaultCount = 50;
    public const int MaxCount = 200;

    /// <summary>
    /// A date range from one or more FHIR date parameters ("2026-10-30",
    /// "ge2026-10-01", "lt2026-11-01", "2026-10"). Bounds are calendar values in
    /// India time; <c>To</c> is exclusive. Returns false on anything unreadable.
    /// </summary>
    public static bool TryParseDates(IEnumerable<string> values, out DateTime? from, out DateTime? toExclusive)
    {
        from = null; toExclusive = null;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var v = raw.Trim();
            var prefix = "eq";
            if (v.Length > 2 && char.IsLetter(v[0]) && char.IsLetter(v[1]))
            {
                prefix = v.Substring(0, 2).ToLowerInvariant();
                v = v.Substring(2);
            }
            if (!TryParsePeriod(v, out var start, out var end)) return false;
            switch (prefix)
            {
                case "eq": from = Max(from, start); toExclusive = Min(toExclusive, end); break;
                case "ge": from = Max(from, start); break;
                case "gt": from = Max(from, end); break;
                case "le": toExclusive = Min(toExclusive, end); break;
                case "lt": toExclusive = Min(toExclusive, start); break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>"2026", "2026-10", "2026-10-30" or a dateTime → [start, end).</summary>
    private static bool TryParsePeriod(string v, out DateTime start, out DateTime end)
    {
        start = end = default;
        var inv = CultureInfo.InvariantCulture;
        if (DateTime.TryParseExact(v, "yyyy", inv, DateTimeStyles.None, out start)) { end = start.AddYears(1); return true; }
        if (DateTime.TryParseExact(v, "yyyy-MM", inv, DateTimeStyles.None, out start)) { end = start.AddMonths(1); return true; }
        if (DateTime.TryParseExact(v, "yyyy-MM-dd", inv, DateTimeStyles.None, out start)) { end = start.AddDays(1); return true; }
        if (DateTimeOffset.TryParse(v, inv, DateTimeStyles.None, out var dto))
        {
            // A full dateTime: express it in India time, as a one-second period.
            start = dto.ToOffset(TimeSpan.FromHours(5.5)).DateTime;
            end = start.AddSeconds(1);
            return true;
        }
        return false;
    }

    private static DateTime? Max(DateTime? a, DateTime b) => a == null || b > a ? b : a;
    private static DateTime? Min(DateTime? a, DateTime b) => a == null || b < a ? b : a;

    /// <summary>"Patient/{id}", "{id}" or a full URL ending in it → the id.</summary>
    public static Guid? ReferenceId(string? value, string resourceType)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().TrimEnd('/');
        var marker = resourceType + "/";
        var at = v.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0) v = v.Substring(at + marker.Length);
        return Guid.TryParse(v, out var g) ? g : null;
    }

    /// <summary>"system|value" or "value" → (system, value). "|value" means no system.</summary>
    public static (string? System, string Value) Token(string raw)
    {
        var bar = raw.IndexOf('|');
        if (bar < 0) return (null, raw.Trim());
        var system = raw.Substring(0, bar).Trim();
        return (system.Length == 0 ? null : system, raw.Substring(bar + 1).Trim());
    }

    public static int Count(string? raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, MaxCount) : DefaultCount;

    public static int Offset(string? raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 0;
}
