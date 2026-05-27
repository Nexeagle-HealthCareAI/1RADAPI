using System.IO.Compression;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.ImageSharp;
using FellowOakDicom.Imaging.NativeCodec;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace _1Rad.Infrastructure.Services;

/// <summary>
/// Extracts ZIP study assets into individual per-slice blobs (Option C).
/// One service per scoped DbContext — instantiate per extraction run.
/// </summary>
public class DicomExtractionService : IDicomExtractionService
{
    private const string Container = "dicom-files";
    private const int ThumbnailMaxDim = 256;

    // Transcode uncompressed DICOM pixel data to JPEG-LS Lossless before
    // uploading to blob storage. Lossless = identical pixel values after
    // decode (primary-diagnosis safe), but typically 3-4x smaller bytes.
    // Frontend Cornerstone wadouri loader transparently decodes the new
    // transfer syntax — no client change required.
    // Flip to false in this service to skip transcoding entirely.
    private const bool TranscodeToJpegLs = true;

    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly ILogger<DicomExtractionService> _logger;

    public DicomExtractionService(
        IApplicationDbContext db,
        IBlobService blob,
        ILogger<DicomExtractionService> logger)
    {
        _db = db;
        _blob = blob;
        _logger = logger;

        // fo-dicom uses a manager pattern — wire ImageSharp + native codecs
        // (JPEG-LS, JPEG2000, JPEG-baseline transcoders) once. Idempotent
        // because the second .Build() call no-ops if already configured.
        try
        {
            new DicomSetupBuilder()
                .RegisterServices(s => s.AddImageManager<ImageSharpImageManager>())
                .RegisterServices(s => s.AddTranscoderManager<NativeTranscoderManager>())
                .SkipValidation()
                .Build();
        }
        catch { /* already configured by another instance — fine */ }
    }

    /// <summary>
    /// Returns JPEG-LS-transcoded bytes for the given DICOM if the source is
    /// uncompressed (Implicit/Explicit VR Little Endian) AND transcoding is
    /// enabled. Otherwise returns the original bytes unchanged.
    ///
    /// Only transcodes from uncompressed sources to avoid:
    ///  - Lossy-to-lossy recompression (quality degradation).
    ///  - Failing on exotic codecs we don't have decoders for.
    /// Falls back to original bytes on any transcode error — the slice
    /// always uploads successfully even if compression fails.
    /// </summary>
    private byte[] TranscodeSliceBytes(DicomFile dicom, byte[] originalBytes, out bool didTranscode)
    {
        didTranscode = false;
        if (!TranscodeToJpegLs) return originalBytes;

        try
        {
            var srcSyntax = dicom.Dataset.InternalTransferSyntax;
            if (srcSyntax == DicomTransferSyntax.JPEGLSLossless)
                return originalBytes; // already JPEG-LS

            var isUncompressed =
                srcSyntax == DicomTransferSyntax.ImplicitVRLittleEndian ||
                srcSyntax == DicomTransferSyntax.ExplicitVRLittleEndian ||
                srcSyntax == DicomTransferSyntax.ExplicitVRBigEndian;
            if (!isUncompressed) return originalBytes;

            var transcoder = new DicomTranscoder(srcSyntax, DicomTransferSyntax.JPEGLSLossless);
            var transcodedFile = transcoder.Transcode(dicom);
            using var ms = new MemoryStream();
            transcodedFile.Save(ms);
            didTranscode = true;
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOM_EXTRACT] JPEG-LS transcode failed — uploading original bytes");
            return originalBytes;
        }
    }

    public async Task<int> ExtractAsync(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await _db.StudyAssets
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
        if (asset == null)
        {
            _logger.LogWarning("[DICOM_EXTRACT] Asset {AssetId} not found, aborting.", assetId);
            return 0;
        }

        // Non-ZIP assets don't get extracted — viewer can load them directly.
        var ext = (asset.FileType ?? "").Trim().ToLowerInvariant();
        if (ext != "zip")
        {
            asset.ExtractionStatus = "NotApplicable";
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        asset.ExtractionStatus = "Running";
        asset.ExtractionStartedAt = DateTime.UtcNow;
        asset.ExtractionError = null;
        await _db.SaveChangesAsync(cancellationToken);

        // Wipe any prior slice index so re-runs are clean. Blobs from a prior
        // run are orphaned in storage — acceptable; periodic cleanup script
        // can sweep them. Keeps the happy path simple.
        var prior = await _db.StudySliceIndexes
            .IgnoreQueryFilters()
            .Where(s => s.AssetId == assetId)
            .ToListAsync(cancellationToken);
        if (prior.Count > 0)
        {
            _db.StudySliceIndexes.RemoveRange(prior);
            await _db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            // 1. Download the ZIP from blob.
            using var zipStream = await _blob.DownloadFileAsync(asset.BlobUrl);
            using var memory = new MemoryStream();
            await zipStream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            using var archive = new ZipArchive(memory, ZipArchiveMode.Read);

            // 2. Parse every entry that looks like DICOM. Group by SeriesUID.
            var parsedSlices = new List<ParsedSlice>(archive.Entries.Count);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length == 0) continue;
                // Skip __MACOSX/ and other zip junk
                if (entry.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Name.StartsWith("._", StringComparison.Ordinal)) continue;

                try
                {
                    using var es = entry.Open();
                    var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
                    await es.CopyToAsync(ms, cancellationToken);
                    ms.Position = 0;

                    var dicom = DicomFile.Open(ms, FileReadOption.ReadAll);
                    var ds = dicom.Dataset;

                    var seriesUid    = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
                    var sopUid       = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID,    string.Empty);
                    if (string.IsNullOrEmpty(seriesUid) || string.IsNullOrEmpty(sopUid))
                    {
                        _logger.LogDebug("[DICOM_EXTRACT] Asset {AssetId} entry {Name} missing UIDs — skipping.", assetId, entry.Name);
                        continue;
                    }

                    parsedSlices.Add(new ParsedSlice
                    {
                        EntryName        = entry.Name,
                        SeriesUid        = seriesUid,
                        SopUid           = sopUid,
                        InstanceNumber   = ds.GetSingleValueOrDefault<int?>(DicomTag.InstanceNumber, null),
                        SeriesDescription = ds.GetSingleValueOrDefault<string?>(DicomTag.SeriesDescription, null),
                        Modality         = ds.GetSingleValueOrDefault<string?>(DicomTag.Modality, null),
                        DicomFile        = dicom,
                        OriginalBytes    = ms.ToArray(),
                    });
                }
                catch (Exception parseEx)
                {
                    // Bad file inside zip — log and move on. Don't fail the whole extraction.
                    _logger.LogDebug(parseEx, "[DICOM_EXTRACT] Asset {AssetId} entry {Name} not a valid DICOM file — skipping.", assetId, entry.Name);
                }
            }

            if (parsedSlices.Count == 0)
                throw new InvalidOperationException("ZIP contained no readable DICOM files.");

            // 3. Group by series and order by InstanceNumber so the blob layout
            // is deterministic and the manifest comes out pre-sorted.
            var bySeries = parsedSlices
                .GroupBy(s => s.SeriesUid)
                .Select((g, idx) => new
                {
                    Index = idx,
                    Slices = g.OrderBy(s => s.InstanceNumber ?? int.MaxValue)
                              .ThenBy(s => s.SopUid)
                              .ToList(),
                })
                .ToList();

            var assetIdN       = asset.Id.ToString("N");
            var hospitalIdN    = asset.HospitalId.ToString("N");
            var appointmentIdN = asset.AppointmentId.ToString("N");
            var sliceCount     = 0;

            // Compression-stats accumulators — logged at end of extraction so
            // we can see the byte savings (and confirm transcoding actually ran).
            long totalOriginalBytes  = 0;
            long totalUploadedBytes  = 0;
            int  transcodedSliceCount = 0;

            // 4. Upload each slice + populate slice index. For the first slice
            // of each series, also generate a thumbnail.
            foreach (var series in bySeries)
            {
                string? thumbnailUrl = null;
                for (int i = 0; i < series.Slices.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var p = series.Slices[i];

                    // Transcode uncompressed pixel data to JPEG-LS Lossless to
                    // shrink the blob payload by ~3-4x. Lossless = primary-
                    // diagnosis safe; falls back to original bytes on any error.
                    var uploadBytes = TranscodeSliceBytes(p.DicomFile, p.OriginalBytes, out var didTranscode);
                    totalOriginalBytes += p.OriginalBytes.Length;
                    totalUploadedBytes += uploadBytes.Length;
                    if (didTranscode) transcodedSliceCount++;

                    // Slice blob path: deterministic, easy to delete by prefix.
                    var sliceBlobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{series.Index:D3}/{i:D4}.dcm";
                    string sliceUrl;
                    using (var sliceUp = new MemoryStream(uploadBytes, writable: false))
                    {
                        sliceUrl = await _blob.UploadFileAtPathAsync(sliceUp, sliceBlobPath, "application/dicom", Container);
                    }

                    // Thumbnail: first slice of each series only.
                    if (i == 0)
                    {
                        try
                        {
                            using var thumb = RenderThumbnail(p.DicomFile);
                            if (thumb != null)
                            {
                                using var ms = new MemoryStream();
                                thumb.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
                                ms.Position = 0;
                                var thumbPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/thumbs/{series.Index:D3}.jpg";
                                thumbnailUrl = await _blob.UploadFileAtPathAsync(ms, thumbPath, "image/jpeg", Container);
                            }
                        }
                        catch (Exception thumbEx)
                        {
                            _logger.LogDebug(thumbEx, "[DICOM_EXTRACT] Asset {AssetId} series {Idx} thumbnail failed — continuing without.", assetId, series.Index);
                        }
                    }

                    _db.StudySliceIndexes.Add(new StudySliceIndex
                    {
                        SliceId           = Guid.NewGuid(),
                        AssetId           = asset.Id,
                        AppointmentId     = asset.AppointmentId,
                        HospitalId        = asset.HospitalId,
                        SeriesUID         = p.SeriesUid,
                        SopInstanceUID    = p.SopUid,
                        InstanceNumber    = p.InstanceNumber,
                        SeriesDescription = Truncate(p.SeriesDescription, 200),
                        Modality          = Truncate(p.Modality, 16),
                        BlobUrl           = sliceUrl,
                        BlobPath          = sliceBlobPath,
                        ThumbnailUrl      = i == 0 ? thumbnailUrl : null,
                        MetadataJson      = ExtractMetadataJson(p.DicomFile),
                        ExtractedAt       = DateTime.UtcNow,
                    });
                    sliceCount++;

                    // Persist in chunks of 100 so a crash mid-extraction isn't a total loss.
                    if (sliceCount % 100 == 0) await _db.SaveChangesAsync(cancellationToken);
                }
            }

            asset.ExtractionStatus      = "Extracted";
            asset.ExtractionSliceCount  = sliceCount;
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            asset.ExtractionError       = null;
            await _db.SaveChangesAsync(cancellationToken);

            // Compression summary — only useful when transcoding ran for >0
            // slices. Helps confirm in prod logs that JPEG-LS is paying off.
            var compressionRatio = totalOriginalBytes > 0
                ? (double)totalUploadedBytes / totalOriginalBytes
                : 1.0;
            _logger.LogInformation(
                "[DICOM_EXTRACT] Asset {AssetId} extracted {Slices} slices across {Series} series. " +
                "Transcoded {Transcoded}/{Slices} slices to JPEG-LS Lossless. " +
                "Bytes uploaded: {Uploaded:N0} (was {Original:N0}, ratio {Ratio:P1}).",
                assetId, sliceCount, bySeries.Count,
                transcodedSliceCount, sliceCount,
                totalUploadedBytes, totalOriginalBytes, compressionRatio);
            return sliceCount;
        }
        catch (Exception ex)
        {
            asset.ExtractionStatus      = "Failed";
            asset.ExtractionError       = Truncate(ex.Message, 2000);
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(ex, "[DICOM_EXTRACT] Asset {AssetId} extraction failed.", assetId);
            throw;
        }
    }

    private static Image? RenderThumbnail(DicomFile file)
    {
        try
        {
            var renderable = new DicomImage(file.Dataset);
            using var rendered = renderable.RenderImage();
            // ImageSharp adapter — Convert to a SharpImage we can encode + resize.
            var sharp = rendered.AsSharpImage();
            var width  = sharp.Width;
            var height = sharp.Height;
            if (width == 0 || height == 0) return null;
            var scale = (double)ThumbnailMaxDim / Math.Max(width, height);
            if (scale < 1.0)
            {
                sharp.Mutate(x => x.Resize((int)(width * scale), (int)(height * scale)));
            }
            return sharp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cherry-picks the viewer-relevant tags into a compact JSON blob so the
    /// manifest stays tiny. Anything missing is omitted.
    /// </summary>
    private static string? ExtractMetadataJson(DicomFile file)
    {
        try
        {
            var ds = file.Dataset;
            var dict = new Dictionary<string, object?>();

            void Add<T>(string key, DicomTag tag) where T : struct
            {
                if (ds.TryGetSingleValue<T>(tag, out var v)) dict[key] = v;
            }
            void AddStr(string key, DicomTag tag)
            {
                if (ds.TryGetSingleValue<string>(tag, out var v) && !string.IsNullOrWhiteSpace(v)) dict[key] = v;
            }

            Add<double>("windowCenter", DicomTag.WindowCenter);
            Add<double>("windowWidth",  DicomTag.WindowWidth);
            Add<double>("rescaleSlope",     DicomTag.RescaleSlope);
            Add<double>("rescaleIntercept", DicomTag.RescaleIntercept);
            Add<double>("sliceLocation",    DicomTag.SliceLocation);
            Add<double>("sliceThickness",   DicomTag.SliceThickness);
            Add<int>("rows",       DicomTag.Rows);
            Add<int>("columns",    DicomTag.Columns);
            Add<int>("bitsStored", DicomTag.BitsStored);
            AddStr("photometricInterpretation", DicomTag.PhotometricInterpretation);
            AddStr("patientPosition",           DicomTag.PatientPosition);

            // Pixel spacing is a multi-value DS — store as array of doubles
            if (ds.Contains(DicomTag.PixelSpacing))
            {
                try
                {
                    var ps = ds.GetValues<double>(DicomTag.PixelSpacing);
                    if (ps?.Length > 0) dict["pixelSpacing"] = ps;
                }
                catch { /* malformed — skip */ }
            }

            return dict.Count == 0 ? null : JsonSerializer.Serialize(dict);
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

    private sealed class ParsedSlice
    {
        public string EntryName         { get; set; } = string.Empty;
        public string SeriesUid         { get; set; } = string.Empty;
        public string SopUid            { get; set; } = string.Empty;
        public int? InstanceNumber      { get; set; }
        public string? SeriesDescription { get; set; }
        public string? Modality         { get; set; }
        public DicomFile DicomFile      { get; set; } = null!;
        public byte[] OriginalBytes     { get; set; } = Array.Empty<byte>();
    }
}
