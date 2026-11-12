namespace _1Rad.Application.Interfaces;

/// <summary>
/// Turns a stored DICOM instance into a plain JPEG for AI drafting: pixels
/// only (no header text), downsized, with the strips where ultrasound and
/// screen-capture machines usually print the patient's name blacked out.
/// </summary>
public interface IKeyImageRenderer
{
    Task<RenderedKeyImage?> RenderAsync(string dicomBlobUrl, int maxDim, CancellationToken ct);
}

/// <param name="Jpeg">The picture.</param>
/// <param name="Modality">DICOM modality (US, CT, …).</param>
/// <param name="StripsMasked">True when the top/bottom strips were blacked out.</param>
public sealed record RenderedKeyImage(byte[] Jpeg, string? Modality, bool StripsMasked);
