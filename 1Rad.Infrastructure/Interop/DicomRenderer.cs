using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// WADO-RS "rendered" and "thumbnail" (PS3.18 §9.5, §9.8): a frame as a consumer
/// image (JPEG or PNG). Grayscale frames go through the modality LUT (rescale)
/// and a linear VOI window — the one in the request, else the file's, else the
/// pixel range; MONOCHROME1 is inverted. RGB / YBR_FULL colour frames are
/// converted to RGB. Everything else (palette colour, YBR_422 left after a
/// decode) is reported as not renderable.
///
/// The pixel maths (<see cref="ToDisplayPixels"/>) is pure and unit-tested;
/// fo-dicom only decodes the frame and ImageSharp only resizes/encodes.
/// </summary>
public static class DicomRenderer
{
    public sealed record RenderOptions(
        string MediaType = "image/jpeg",
        int Quality = 90,
        int? ViewportWidth = null,
        int? ViewportHeight = null,
        double? WindowCenter = null,
        double? WindowWidth = null);

    public sealed record PixelFrame(
        byte[] Data, int Rows, int Columns, int SamplesPerPixel, int BitsAllocated, bool Signed,
        double Slope, double Intercept, string Photometric, int PlanarConfiguration,
        double? WindowCenter, double? WindowWidth);

    /// <summary>8-bit display pixels: one byte per pixel (gray) or three (RGB). Null when not renderable.</summary>
    public static (byte[] Pixels, bool Rgb)? ToDisplayPixels(PixelFrame f, double? wcOverride = null, double? wwOverride = null)
    {
        var n = f.Rows * f.Columns;
        if (n <= 0) return null;
        var photometric = (f.Photometric ?? "MONOCHROME2").Trim().ToUpperInvariant();

        if (f.SamplesPerPixel == 3)
        {
            if (f.BitsAllocated != 8 || f.Data.Length < n * 3) return null;
            var rgb = new byte[n * 3];
            for (var i = 0; i < n; i++)
            {
                byte a, b, c;
                if (f.PlanarConfiguration == 1) { a = f.Data[i]; b = f.Data[n + i]; c = f.Data[2 * n + i]; }
                else { a = f.Data[i * 3]; b = f.Data[i * 3 + 1]; c = f.Data[i * 3 + 2]; }
                if (photometric == "RGB")
                {
                    rgb[i * 3] = a; rgb[i * 3 + 1] = b; rgb[i * 3 + 2] = c;
                }
                else if (photometric == "YBR_FULL")
                {
                    double y = a, cb = b - 128.0, cr = c - 128.0;
                    rgb[i * 3] = Clamp(y + 1.402 * cr);
                    rgb[i * 3 + 1] = Clamp(y - 0.344136 * cb - 0.714136 * cr);
                    rgb[i * 3 + 2] = Clamp(y + 1.772 * cb);
                }
                else return null;
            }
            return (rgb, true);
        }

        if (f.SamplesPerPixel != 1 || (photometric != "MONOCHROME1" && photometric != "MONOCHROME2")) return null;
        var bytesPerPx = f.BitsAllocated > 8 ? 2 : 1;
        if (f.Data.Length < n * bytesPerPx) return null;

        double Read(int i) => bytesPerPx == 2
            ? (f.Signed ? BitConverter.ToInt16(f.Data, i * 2) : BitConverter.ToUInt16(f.Data, i * 2))
            : (f.Signed ? (sbyte)f.Data[i] : f.Data[i]);

        var slope = f.Slope == 0 ? 1.0 : f.Slope;
        var wc = wcOverride ?? f.WindowCenter;
        var ww = wwOverride ?? f.WindowWidth;
        if (wc == null || ww == null || ww < 1)
        {
            double mn = double.MaxValue, mx = double.MinValue;
            for (var i = 0; i < n; i++)
            {
                var v = Read(i) * slope + f.Intercept;
                if (v < mn) mn = v;
                if (v > mx) mx = v;
            }
            wc = (mn + mx) / 2.0;
            ww = Math.Max(1.0, mx - mn);
        }

        var gray = new byte[n];
        var lo = wc.Value - 0.5 - (ww.Value - 1) / 2.0;   // PS3.3 C.11.2.1.2 linear VOI
        var span = Math.Max(1.0, ww.Value - 1);
        var invert = photometric == "MONOCHROME1";
        for (var i = 0; i < n; i++)
        {
            var v = Read(i) * slope + f.Intercept;
            var g = (int)Math.Round(Math.Clamp((v - lo) / span, 0.0, 1.0) * 255);
            gray[i] = (byte)(invert ? 255 - g : g);
        }
        return (gray, false);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    /// <summary>Render frame <paramref name="frameIndex"/> (0-based) of a stored file. Null when not renderable.</summary>
    public static byte[]? Render(byte[] dicomBytes, int frameIndex, RenderOptions options)
    {
        DicomCodecs.Ensure();
        using var ms = new MemoryStream(dicomBytes, writable: false);
        var file = DicomFile.Open(ms, FileReadOption.ReadAll);
        var ds = file.Dataset;
        if (!ds.Contains(DicomTag.PixelData)) return null;

        var stored = file.FileMetaInfo.TransferSyntax;
        var pixel = DicomPixelData.Create(ds);
        if (frameIndex < 0 || frameIndex >= pixel.NumberOfFrames) return null;
        var data = stored.IsEncapsulated
            ? new DicomTranscoder(stored, DicomTransferSyntax.ExplicitVRLittleEndian).DecodeFrame(ds, frameIndex).Data
            : pixel.GetFrame(frameIndex).Data;

        // Decoding JPEG baseline colour yields RGB pixels even if the header still says YBR.
        var photometric = ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2");
        if (stored.IsEncapsulated && photometric.StartsWith("YBR", StringComparison.OrdinalIgnoreCase)) photometric = "RGB";

        var frame = new PixelFrame(
            data,
            ds.GetSingleValueOrDefault<ushort>(DicomTag.Rows, 0),
            ds.GetSingleValueOrDefault<ushort>(DicomTag.Columns, 0),
            ds.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 1),
            ds.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 16),
            ds.GetSingleValueOrDefault<ushort>(DicomTag.PixelRepresentation, 0) == 1,
            ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0),
            ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0),
            photometric,
            stored.IsEncapsulated ? 0 : ds.GetSingleValueOrDefault<ushort>(DicomTag.PlanarConfiguration, 0),
            FirstOrNull(ds, DicomTag.WindowCenter),
            FirstOrNull(ds, DicomTag.WindowWidth));

        var display = ToDisplayPixels(frame, options.WindowCenter, options.WindowWidth);
        if (display == null) return null;
        return Encode(display.Value.Pixels, display.Value.Rgb, frame.Columns, frame.Rows, options);
    }

    private static double? FirstOrNull(DicomDataset ds, DicomTag tag)
    {
        try
        {
            return ds.Contains(tag) ? ds.GetValue<double>(tag, 0) : null;
        }
        catch
        {
            return null;
        }
    }

    public static byte[] Encode(byte[] pixels, bool rgb, int width, int height, RenderOptions options)
    {
        using Image image = rgb
            ? Image.LoadPixelData<Rgb24>(pixels, width, height)
            : Image.LoadPixelData<L8>(pixels, width, height);

        var (w, h) = Fit(width, height, options.ViewportWidth, options.ViewportHeight);
        if (w != width || h != height) image.Mutate(x => x.Resize(w, h));

        using var outMs = new MemoryStream();
        if (options.MediaType == "image/png") image.SaveAsPng(outMs, new PngEncoder());
        else image.SaveAsJpeg(outMs, new JpegEncoder { Quality = Math.Clamp(options.Quality, 1, 100) });
        return outMs.ToArray();
    }

    /// <summary>Scale down (never up) to fit the viewport, keeping the aspect ratio.</summary>
    public static (int W, int H) Fit(int width, int height, int? vw, int? vh)
    {
        if (vw is not > 0 && vh is not > 0) return (width, height);
        var sx = vw is > 0 ? (double)vw.Value / width : double.MaxValue;
        var sy = vh is > 0 ? (double)vh.Value / height : double.MaxValue;
        var s = Math.Min(1.0, Math.Min(sx, sy));
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }

    /// <summary>
    /// Read the PS3.18 rendering query parameters: quality, viewport=vw,vh (or
    /// vw,vh,sx,sy,sw,sh — the crop part is ignored), window=center,width[,function].
    /// </summary>
    public static RenderOptions ParseQuery(string? quality, string? viewport, string? window, string mediaType)
    {
        var q = int.TryParse(quality, out var qq) ? Math.Clamp(qq, 1, 100) : 90;
        int? vw = null, vh = null;
        if (!string.IsNullOrWhiteSpace(viewport))
        {
            var p = viewport.Split(',');
            if (p.Length >= 1 && int.TryParse(p[0], out var a) && a > 0) vw = a;
            if (p.Length >= 2 && int.TryParse(p[1], out var b) && b > 0) vh = b;
        }
        double? wc = null, ww = null;
        if (!string.IsNullOrWhiteSpace(window))
        {
            var p = window.Split(',');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length >= 2
                && double.TryParse(p[0], System.Globalization.NumberStyles.Float, inv, out var c)
                && double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var w) && w >= 1)
            {
                wc = c; ww = w;
            }
        }
        return new RenderOptions(mediaType, q, vw, vh, wc, ww);
    }

    /// <summary>image/png when the Accept header prefers it, else image/jpeg; null when it allows neither.</summary>
    public static string? MediaType(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return "image/jpeg";
        string? best = null;
        double bestQ = -1;
        foreach (var range in accept.Split(','))
        {
            var parts = range.Split(';');
            var type = parts[0].Trim().ToLowerInvariant();
            var q = 1.0;
            foreach (var p in parts.Skip(1))
            {
                var kv = p.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim() == "q"
                    && double.TryParse(kv[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var qq)) q = qq;
            }
            if (q <= 0) continue;
            var resolved = type switch
            {
                "image/png" => "image/png",
                "image/jpeg" or "image/jpg" or "image/*" or "*/*" => "image/jpeg",
                _ => null,
            };
            if (resolved != null && q > bestQ) { best = resolved; bestQ = q; }
        }
        return best;
    }
}
