using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// Registers fo-dicom's native codecs (HTJ2K, JPEG 2000, JPEG-LS, JPEG) once per
/// process — the same setup <c>DicomExtractionService</c> does — so DICOMweb can
/// decode or re-encode stored slices even if no extraction has run yet.
/// </summary>
internal static class DicomCodecs
{
    private static int _done;

    public static void Ensure()
    {
        if (Interlocked.Exchange(ref _done, 1) == 1) return;
        try
        {
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddTranscoderManager<NativeTranscoderManager>())
                .SkipValidation()
                .Build();
        }
        catch
        {
            // Already configured by the extraction service — fine.
        }
    }
}
