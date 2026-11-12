namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// Reads a DICOMweb <c>Accept</c> header (PS3.18 §8.7) such as
/// <c>multipart/related; type="application/dicom"; transfer-syntax=*</c>
/// into the media type the client wants inside the multipart body and the
/// transfer syntax it will decode.
/// </summary>
public static class DicomWebAccept
{
    public const string ExplicitVrLittleEndian = "1.2.840.10008.1.2.1";
    public const string AnySyntax = "*";

    public sealed record Choice(string MediaType, string? TransferSyntax, double Quality);

    /// <summary>
    /// Every acceptable choice, best first. Bare <c>multipart/related</c> without a
    /// type parameter and <c>*/*</c> are reported with an empty MediaType ("anything").
    /// </summary>
    public static List<Choice> Parse(string? header)
    {
        var result = new List<Choice>();
        if (string.IsNullOrWhiteSpace(header)) return result;

        foreach (var range in SplitOutsideQuotes(header, ','))
        {
            var parts = SplitOutsideQuotes(range, ';').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) continue;
            var outer = parts[0].ToLowerInvariant();
            string? type = null, ts = null;
            double q = 1.0;
            foreach (var p in parts.Skip(1))
            {
                var eq = p.IndexOf('=');
                if (eq <= 0) continue;
                var name = p.Substring(0, eq).Trim().ToLowerInvariant();
                var value = p.Substring(eq + 1).Trim().Trim('"');
                if (name == "type") type = value.ToLowerInvariant();
                else if (name == "transfer-syntax") ts = value;
                else if (name == "q" && double.TryParse(value, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var qq)) q = qq;
            }

            string media;
            if (outer == "multipart/related") media = type ?? string.Empty;
            else if (outer == "*/*" || outer == "multipart/*") media = string.Empty;
            else media = outer; // a single-part request such as application/dicom or application/dicom+json
            if (q <= 0) continue;
            result.Add(new Choice(media, ts, q));
        }
        return result.OrderByDescending(c => c.Quality).ToList();
    }

    /// <summary>
    /// For instance retrieval: the transfer syntax to send, given what the file is
    /// stored in. PS3.18 says a request that names no transfer syntax gets Explicit
    /// VR Little Endian, so only "*" (or the stored UID itself) keeps the stored
    /// encoding. Returns null when no acceptable choice includes application/dicom.
    /// </summary>
    public static string? InstanceSyntax(string? header, string storedSyntax)
    {
        var choices = Parse(header);
        if (choices.Count == 0) return ExplicitVrLittleEndian;
        foreach (var c in choices)
        {
            if (c.MediaType.Length != 0 && c.MediaType != "application/dicom") continue;
            if (c.TransferSyntax == null) return ExplicitVrLittleEndian;
            if (c.TransferSyntax == AnySyntax) return storedSyntax;
            return c.TransferSyntax;
        }
        return null;
    }

    /// <summary>
    /// For frame retrieval: (transfer syntax, part content type). The stored
    /// (compressed) frame is sent when the client accepts "*" or the stored syntax;
    /// otherwise the frame is decoded and sent as uncompressed octet-stream.
    /// Returns null when no choice can be met.
    /// </summary>
    public static (string Syntax, string ContentType)? FrameSyntax(string? header, string storedSyntax, string storedMediaType)
    {
        var choices = Parse(header);
        if (choices.Count == 0)
            return (ExplicitVrLittleEndian, "application/octet-stream");
        foreach (var c in choices)
        {
            var media = c.MediaType;
            if (media.Length == 0 || media == "application/octet-stream")
            {
                // "octet-stream; transfer-syntax=*" is how OHIF/Cornerstone ask for frames
                // "as stored": answer with octet-stream labelled with the real syntax
                // (what Orthanc and dcm4chee do).
                if (c.TransferSyntax == AnySyntax || c.TransferSyntax == storedSyntax)
                    return (storedSyntax, "application/octet-stream");
                if (c.TransferSyntax == null || IsUncompressed(c.TransferSyntax))
                    return (ExplicitVrLittleEndian, "application/octet-stream");
                continue;
            }
            if (media == storedMediaType && (c.TransferSyntax == null || c.TransferSyntax == AnySyntax || c.TransferSyntax == storedSyntax))
                return (storedSyntax, storedMediaType);
        }
        return null;
    }

    public static bool IsUncompressed(string syntax) =>
        syntax == "1.2.840.10008.1.2" || syntax == "1.2.840.10008.1.2.1" || syntax == "1.2.840.10008.1.2.2";

    /// <summary>Media type for one compressed frame (PS3.18 Table 8.7.3-2).</summary>
    public static string FrameMediaType(string syntax) => syntax switch
    {
        "1.2.840.10008.1.2.4.50" or "1.2.840.10008.1.2.4.51" or "1.2.840.10008.1.2.4.57" or "1.2.840.10008.1.2.4.70" => "image/jpeg",
        "1.2.840.10008.1.2.4.80" or "1.2.840.10008.1.2.4.81" => "image/jls",
        "1.2.840.10008.1.2.4.90" or "1.2.840.10008.1.2.4.91" => "image/jp2",
        "1.2.840.10008.1.2.4.92" or "1.2.840.10008.1.2.4.93" => "image/jpx",
        "1.2.840.10008.1.2.4.201" or "1.2.840.10008.1.2.4.202" or "1.2.840.10008.1.2.4.203" => "image/jphc",
        "1.2.840.10008.1.2.5" => "image/dicom-rle",
        _ => "application/octet-stream",
    };

    private static IEnumerable<string> SplitOutsideQuotes(string s, char sep)
    {
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '"') inQuotes = !inQuotes;
            else if (s[i] == sep && !inQuotes)
            {
                yield return s.Substring(start, i - start);
                start = i + 1;
            }
        }
        yield return s.Substring(start);
    }
}
