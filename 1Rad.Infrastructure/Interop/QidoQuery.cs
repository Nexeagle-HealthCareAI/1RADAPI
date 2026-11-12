using System.Globalization;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// The QIDO-RS search parameters 1Rad understands (PS3.18 §10.6), read from a
/// query string. Attributes may be given by keyword (PatientName) or tag
/// (00100010). Unknown parameters are ignored, as the standard allows.
/// </summary>
public sealed class QidoQuery
{
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public List<string> StudyInstanceUids { get; } = new();
    public List<string> SeriesInstanceUids { get; } = new();
    public List<string> SopInstanceUids { get; } = new();
    public string? PatientName { get; set; }
    public string? PatientId { get; set; }
    public string? AccessionNumber { get; set; }
    public string? StudyDescription { get; set; }
    public string? SeriesDescription { get; set; }
    public string? Modality { get; set; }
    public DateTime? StudyDateFrom { get; set; }
    /// <summary>Exclusive upper bound (the day after the last day asked for).</summary>
    public DateTime? StudyDateToExclusive { get; set; }
    public bool Fuzzy { get; set; }
    public int Limit { get; set; } = DefaultLimit;
    public int Offset { get; set; }
    /// <summary>True when the StudyDate value could not be read — the search then matches nothing.</summary>
    public bool InvalidDate { get; set; }

    private static readonly Dictionary<string, string> TagToKeyword = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0020000D"] = "StudyInstanceUID",
        ["0020000E"] = "SeriesInstanceUID",
        ["00080018"] = "SOPInstanceUID",
        ["00100010"] = "PatientName",
        ["00100020"] = "PatientID",
        ["00080050"] = "AccessionNumber",
        ["00080020"] = "StudyDate",
        ["00080061"] = "ModalitiesInStudy",
        ["00080060"] = "Modality",
        ["00081030"] = "StudyDescription",
        ["0008103E"] = "SeriesDescription",
    };

    public static QidoQuery Parse(IEnumerable<KeyValuePair<string, string?>> query)
    {
        var q = new QidoQuery();
        foreach (var (rawKey, rawValue) in query)
        {
            var value = rawValue?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            var key = TagToKeyword.TryGetValue(rawKey, out var kw) ? kw : rawKey;
            switch (key.ToLowerInvariant())
            {
                case "studyinstanceuid": q.StudyInstanceUids.AddRange(SplitList(value)); break;
                case "seriesinstanceuid": q.SeriesInstanceUids.AddRange(SplitList(value)); break;
                case "sopinstanceuid": q.SopInstanceUids.AddRange(SplitList(value)); break;
                case "patientname": q.PatientName = value; break;
                case "patientid": q.PatientId = value; break;
                case "accessionnumber": q.AccessionNumber = value; break;
                case "studydescription": q.StudyDescription = value; break;
                case "seriesdescription": q.SeriesDescription = value; break;
                case "modalitiesinstudy":
                case "modality": q.Modality = value; break;
                case "studydate":
                    if (TryParseDateRange(value, out var from, out var to)) { q.StudyDateFrom = from; q.StudyDateToExclusive = to; }
                    else q.InvalidDate = true;
                    break;
                case "fuzzymatching": q.Fuzzy = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "limit":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var l))
                        q.Limit = Math.Clamp(l, 1, MaxLimit);
                    break;
                case "offset":
                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var o))
                        q.Offset = Math.Max(0, o);
                    break;
            }
        }
        return q;
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split(new[] { ',', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// DICOM date matching: "20260101", "20260101-20260131", "-20260131", "20260101-".
    /// Upper bound is returned exclusive (midnight after the last day).
    /// </summary>
    public static bool TryParseDateRange(string value, out DateTime? from, out DateTime? toExclusive)
    {
        from = null; toExclusive = null;
        value = value.Trim();
        var dash = value.IndexOf('-');
        if (dash < 0)
        {
            if (!TryParseDate(value, out var d)) return false;
            from = d; toExclusive = d.AddDays(1);
            return true;
        }
        var left = value.Substring(0, dash).Trim();
        var right = value.Substring(dash + 1).Trim();
        if (left.Length == 0 && right.Length == 0) return false;
        if (left.Length > 0)
        {
            if (!TryParseDate(left, out var d)) return false;
            from = d;
        }
        if (right.Length > 0)
        {
            if (!TryParseDate(right, out var d)) return false;
            toExclusive = d.AddDays(1);
        }
        return true;
    }

    private static bool TryParseDate(string s, out DateTime date) =>
        DateTime.TryParseExact(s.Replace(".", ""), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// A DICOM wildcard value ('*' any run, '?' one character) as a SQL LIKE
    /// pattern; plain values match exactly unless <paramref name="contains"/>.
    /// Returns null for "*" alone (matches everything).
    /// </summary>
    public static string? ToLikePattern(string value, bool contains)
    {
        var v = value.Trim();
        if (v == "*") return null;
        var sb = new System.Text.StringBuilder();
        foreach (var c in v)
        {
            switch (c)
            {
                case '*': sb.Append('%'); break;
                case '?': sb.Append('_'); break;
                case '%': sb.Append("[%]"); break;
                case '_': sb.Append("[_]"); break;
                case '[': sb.Append("[[]"); break;
                case '^': sb.Append(' '); break; // DICOM name separator — 1Rad stores "First Last"
                default: sb.Append(c); break;
            }
        }
        var p = sb.ToString();
        return contains ? $"%{p.Trim('%')}%" : p;
    }
}
