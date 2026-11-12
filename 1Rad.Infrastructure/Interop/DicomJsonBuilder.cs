using System.Text.Json.Nodes;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// Small helpers for writing DICOM JSON (PS3.18 Annex F) by hand — used where the
/// attributes come from 1Rad's own tables (QIDO-RS results, the modality worklist,
/// STOW-RS responses) rather than from a parsed DICOM file.
///
/// Every helper skips the attribute entirely when the value is missing, except
/// <see cref="Empty"/>, which writes a present-but-empty element.
/// </summary>
public static class DicomJsonBuilder
{
    public static JsonObject New() => new();

    /// <summary>Text-like VRs: AE AS CS DA DS DT IS LO LT SH ST TM UC UI UR UT.</summary>
    public static JsonObject Str(this JsonObject o, string tag, string vr, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return o;
        o[tag] = new JsonObject { ["vr"] = vr, ["Value"] = new JsonArray(value.Trim()) };
        return o;
    }

    /// <summary>Multi-valued text attribute (e.g. ModalitiesInStudy).</summary>
    public static JsonObject Strs(this JsonObject o, string tag, string vr, IEnumerable<string?> values)
    {
        var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return o;
        var arr = new JsonArray();
        foreach (var v in list) arr.Add(v);
        o[tag] = new JsonObject { ["vr"] = vr, ["Value"] = arr };
        return o;
    }

    /// <summary>Person name, written as its Alphabetic component.</summary>
    public static JsonObject Pn(this JsonObject o, string tag, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return o;
        o[tag] = new JsonObject
        {
            ["vr"] = "PN",
            ["Value"] = new JsonArray(new JsonObject { ["Alphabetic"] = name.Trim() }),
        };
        return o;
    }

    /// <summary>Integer VRs written as JSON numbers: IS, SL, SS, UL, US.</summary>
    public static JsonObject Int(this JsonObject o, string tag, string vr, long? value)
    {
        if (value == null) return o;
        o[tag] = new JsonObject { ["vr"] = vr, ["Value"] = new JsonArray(value.Value) };
        return o;
    }

    /// <summary>DA (YYYYMMDD) from a date.</summary>
    public static JsonObject Date(this JsonObject o, string tag, DateTime? value) =>
        value == null ? o : o.Str(tag, "DA", value.Value.ToString("yyyyMMdd"));

    /// <summary>TM (HHMMSS) from a time of day.</summary>
    public static JsonObject Time(this JsonObject o, string tag, DateTime? value) =>
        value == null ? o : o.Str(tag, "TM", value.Value.ToString("HHmmss"));

    /// <summary>A sequence of items.</summary>
    public static JsonObject Seq(this JsonObject o, string tag, IEnumerable<JsonObject> items)
    {
        var arr = new JsonArray();
        foreach (var i in items) arr.Add(i);
        o[tag] = new JsonObject { ["vr"] = "SQ", ["Value"] = arr };
        return o;
    }

    /// <summary>A present attribute with no value (type 2 "empty").</summary>
    public static JsonObject Empty(this JsonObject o, string tag, string vr)
    {
        o[tag] = new JsonObject { ["vr"] = vr };
        return o;
    }

    /// <summary>
    /// DICOM SH/LO/PN text must not carry backslashes (the value separator) or
    /// control characters, and has a length limit. Use on free text from 1Rad.
    /// </summary>
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Where(c => c != '\\' && !char.IsControl(c)).ToArray();
        var s = new string(chars).Trim();
        if (s.Length == 0) return null;
        return s.Length > maxLength ? s.Substring(0, maxLength).TrimEnd() : s;
    }

    /// <summary>
    /// DICOM sex code from 1Rad's free-text gender: M, F, O (or null when unknown).
    /// </summary>
    public static string? Sex(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender)) return null;
        var g = gender.Trim().ToUpperInvariant();
        if (g.StartsWith("M")) return "M";
        if (g.StartsWith("F") || g.StartsWith("W")) return "F";
        return "O";
    }

    /// <summary>
    /// DICOM AS (age string, e.g. "045Y") from 1Rad's free-text age such as
    /// "45", "45 Y", "6 months", "10d". Null when it can't be read.
    /// </summary>
    public static string? Age(string? age)
    {
        if (string.IsNullOrWhiteSpace(age)) return null;
        var s = age.Trim().ToUpperInvariant();
        var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var n) || n < 0 || n > 999) return null;
        var unit = s.Substring(digits.Length).Trim();
        var u = unit.StartsWith("D") ? 'D'
              : unit.StartsWith("W") ? 'W'
              : unit.StartsWith("M") ? 'M'
              : 'Y';
        return n.ToString("D3") + u;
    }
}
