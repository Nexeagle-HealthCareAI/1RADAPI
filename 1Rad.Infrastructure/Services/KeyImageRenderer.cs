using _1Rad.Application.Interfaces;
using _1Rad.Infrastructure.Interop;
using FellowOakDicom;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Renders a stored DICOM slice to a JPEG for AI drafting (see
/// <see cref="IKeyImageRenderer"/>), through the same tested pixel path as the
/// DICOMweb "rendered" endpoint (<see cref="DicomRenderer"/>).
///
/// Burned-in text: ultrasound, secondary-capture and other "photo" modalities,
/// and any image flagged BurnedInAnnotation = YES, get the top 12% and bottom
/// 7% blacked out — where machines usually print name, ID and date. That
/// covers the common layouts, not every machine.
/// </summary>
public class KeyImageRenderer : IKeyImageRenderer
{
    private static readonly HashSet<string> PhotoModalities = new(StringComparer.OrdinalIgnoreCase)
        { "US", "SC", "OT", "XC", "ES", "GM", "SM", "IVUS", "OCT", "DOC" };

    private readonly IBlobService _blob;
    private readonly ILogger<KeyImageRenderer> _logger;

    public KeyImageRenderer(IBlobService blob, ILogger<KeyImageRenderer> logger)
    {
        _blob = blob;
        _logger = logger;
    }

    public async Task<RenderedKeyImage?> RenderAsync(string dicomBlobUrl, int maxDim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dicomBlobUrl)) return null;
        try
        {
            byte[] bytes;
            await using (var stream = await _blob.DownloadFileAsync(dicomBlobUrl))
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }

            string modality;
            bool burnedIn;
            DicomCodecs.Ensure();
            using (var head = new MemoryStream(bytes, writable: false))
            {
                var ds = DicomFile.Open(head, FileReadOption.SkipLargeTags).Dataset;
                modality = ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
                burnedIn = string.Equals(ds.GetSingleValueOrDefault(DicomTag.BurnedInAnnotation, "NO"), "YES", StringComparison.OrdinalIgnoreCase);
            }
            var mask = burnedIn || PhotoModalities.Contains(modality);

            var jpeg = DicomRenderer.Render(bytes, 0,
                new DicomRenderer.RenderOptions("image/jpeg", 88, maxDim, maxDim),
                mask ? MaskStrips : null);
            return jpeg == null ? null : new RenderedKeyImage(jpeg, string.IsNullOrEmpty(modality) ? null : modality, mask);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[KEY_IMAGE] render failed");
            return null;
        }
    }

    /// <summary>Black out the strips where machines print name / ID / date.</summary>
    public static void MaskStrips(byte[] pixels, bool rgb, int width, int height)
    {
        var bpp = rgb ? 3 : 1;
        var top = (int)Math.Ceiling(height * 0.12);
        var bottom = (int)Math.Ceiling(height * 0.07);
        Array.Clear(pixels, 0, Math.Min(pixels.Length, top * width * bpp));
        var start = Math.Max(0, (height - bottom) * width * bpp);
        Array.Clear(pixels, start, Math.Max(0, Math.Min(pixels.Length - start, bottom * width * bpp)));
    }
}
