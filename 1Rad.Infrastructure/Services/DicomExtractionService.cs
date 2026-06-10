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

    // Extracted slices + thumbnails are immutable (keyed by asset/series/instance),
    // so tell browsers and any CDN to cache them for a year. This turns repeat
    // study views into local cache hits instead of re-downloading every slice.
    private const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    // Transcode every slice to HTJ2K Lossless RPCL (High-Throughput JPEG 2000,
    // transfer syntax 1.2.840.10008.1.2.4.202) before uploading to blob.
    // Lossless = identical pixel values after decode (primary-diagnosis safe),
    // and the RPCL (Resolution-Position-Component-Layer) progression order is
    // what lets the viewer pull a low-res preview from the first few KB via a
    // byte-range request, then sharpen — the basis of progressive 2D loading
    // and decimated MPR. The frontend decodes it with @cornerstonejs/codec-
    // openjph (already installed); pre-existing JPEG-LS studies keep working
    // via the charls codec, so this needs no client change and no forced
    // re-extraction. Flip to false to upload original bytes untouched.
    private const bool TranscodeToHtj2k = true;

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
    /// Returns HTJ2K-Lossless-RPCL-transcoded bytes for the given DICOM, or the
    /// original bytes if the slice is already HTJ2K, transcoding is disabled, or
    /// the source codec can't be decoded.
    ///
    /// Unlike the previous JPEG-LS path we transcode from ANY source syntax, not
    /// just uncompressed: HTJ2K RPCL is LOSSLESS, so re-encoding an already-lossy
    /// source introduces no FURTHER loss, and getting every slice into HTJ2K is
    /// the prerequisite for byte-range progressive loading + decimated MPR. The
    /// decode happens inside fo-dicom.Codecs (OpenJPH); on any failure (exotic
    /// source codec, etc.) we fall back to the original bytes so the slice always
    /// uploads successfully.
    /// </summary>
    private byte[] TranscodeSliceBytes(DicomFile dicom, byte[] originalBytes, out bool didTranscode)
    {
        didTranscode = false;
        if (!TranscodeToHtj2k) return originalBytes;

        try
        {
            var srcSyntax = dicom.Dataset.InternalTransferSyntax;
            // Already an HTJ2K variant — re-encoding would only burn CPU.
            if (srcSyntax == DicomTransferSyntax.HTJ2KLosslessRPCL ||
                srcSyntax == DicomTransferSyntax.HTJ2KLossless ||
                srcSyntax == DicomTransferSyntax.HTJ2K)
                return originalBytes;

            var transcoder = new DicomTranscoder(srcSyntax, DicomTransferSyntax.HTJ2KLosslessRPCL);
            var transcodedFile = transcoder.Transcode(dicom);
            using var ms = new MemoryStream();
            transcodedFile.Save(ms);
            didTranscode = true;
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOM_EXTRACT] HTJ2K transcode failed — uploading original bytes");
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

        // Only ZIP archives and per-instance ("instances") uploads get
        // extracted into per-slice blobs. Single attachments (dcm/jpg/png)
        // load directly in the viewer.
        var ext = (asset.FileType ?? "").Trim().ToLowerInvariant();
        if (ext != "zip" && ext != "instances")
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
            // 1+2. Gather DICOM slices from the source — either a single ZIP
            // archive (legacy) or a set of pre-staged per-instance blobs (the
            // bridge's SAS-per-file path). Both yield a flat list of parsed
            // slices; everything downstream (grouping, transcode, index,
            // thumbnails) is identical.
            var parsedSlices = ext == "instances"
                ? await GatherSlicesFromStagedInstancesAsync(asset, cancellationToken)
                : await GatherSlicesFromZipAsync(asset, cancellationToken);

            if (parsedSlices.Count == 0)
                throw new InvalidOperationException("No readable DICOM instances found in the upload.");

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

                    // Transcode to HTJ2K Lossless RPCL — shrinks the payload and,
                    // crucially, makes each slice resolution-progressive (a low-res
                    // image decodes from a byte-range prefix). Lossless = primary-
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
                        sliceUrl = await _blob.UploadFileAtPathAsync(sliceUp, sliceBlobPath, "application/dicom", Container, ImmutableCacheControl);
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
                                thumbnailUrl = await _blob.UploadFileAtPathAsync(ms, thumbPath, "image/jpeg", Container, ImmutableCacheControl);
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
                "Transcoded {Transcoded}/{Slices} slices to HTJ2K Lossless RPCL. " +
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

    // Parse one downloaded DICOM byte stream into a ParsedSlice, or null if it
    // isn't a readable DICOM with the UIDs we need. Shared by both gatherers.
    private ParsedSlice? ParseSlice(Guid assetId, string label, byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var dicom = DicomFile.Open(ms, FileReadOption.ReadAll);
            var ds = dicom.Dataset;
            var seriesUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
            var sopUid    = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID,    string.Empty);
            if (string.IsNullOrEmpty(seriesUid) || string.IsNullOrEmpty(sopUid))
            {
                _logger.LogDebug("[DICOM_EXTRACT] Asset {AssetId} {Label} missing UIDs — skipping.", assetId, label);
                return null;
            }
            return new ParsedSlice
            {
                EntryName         = label,
                SeriesUid         = seriesUid,
                SopUid            = sopUid,
                InstanceNumber    = ds.GetSingleValueOrDefault<int?>(DicomTag.InstanceNumber, null),
                SeriesDescription = ds.GetSingleValueOrDefault<string?>(DicomTag.SeriesDescription, null),
                Modality          = ds.GetSingleValueOrDefault<string?>(DicomTag.Modality, null),
                DicomFile         = dicom,
                OriginalBytes     = bytes,
            };
        }
        catch (Exception parseEx)
        {
            _logger.LogDebug(parseEx, "[DICOM_EXTRACT] Asset {AssetId} {Label} not a valid DICOM file — skipping.", assetId, label);
            return null;
        }
    }

    // Legacy path: download the study ZIP from blob and parse every entry.
    private async Task<List<ParsedSlice>> GatherSlicesFromZipAsync(StudyAsset asset, CancellationToken ct)
    {
        using var zipStream = await _blob.DownloadFileAsync(asset.BlobUrl);
        using var memory = new MemoryStream();
        await zipStream.CopyToAsync(memory, ct);
        memory.Position = 0;

        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var parsed = new List<ParsedSlice>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Length == 0) continue;
            if (entry.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Name.StartsWith("._", StringComparison.Ordinal)) continue;

            using var es = entry.Open();
            using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            await es.CopyToAsync(ms, ct);
            var slice = ParseSlice(asset.Id, entry.Name, ms.ToArray());
            if (slice != null) parsed.Add(slice);
        }
        return parsed;
    }

    // Per-instance path: asset.BlobUrl points to a small JSON manifest listing
    // the pre-staged instance blob URLs (the bridge PUT each .dcm via SAS).
    // Download + parse each, then best-effort delete the staging blobs so they
    // don't linger (the final transcoded slices are written elsewhere).
    private async Task<List<ParsedSlice>> GatherSlicesFromStagedInstancesAsync(StudyAsset asset, CancellationToken ct)
    {
        using var manifestStream = await _blob.DownloadFileAsync(asset.BlobUrl);
        using var mm = new MemoryStream();
        await manifestStream.CopyToAsync(mm, ct);
        var manifest = System.Text.Json.JsonSerializer.Deserialize<StagedManifest>(
            System.Text.Encoding.UTF8.GetString(mm.ToArray())) ?? new StagedManifest();

        var urls = manifest.Instances ?? new List<string>();
        var parsed = new List<ParsedSlice>(urls.Count);
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                using var s = await _blob.DownloadFileAsync(url);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            catch (Exception dlEx)
            {
                _logger.LogDebug(dlEx, "[DICOM_EXTRACT] Asset {AssetId} staged instance {Url} download failed — skipping.", asset.Id, url);
                continue;
            }
            var slice = ParseSlice(asset.Id, url, bytes);
            if (slice != null) parsed.Add(slice);
        }

        // NOTE: staging blobs are intentionally left in place. Deleting here
        // would break an extraction retry (the bytes are only in memory for this
        // run). They live under the staging/{assetId}/ prefix and are reclaimed
        // by a periodic sweep; the real slices are written under extracted/.
        return parsed;
    }

    private sealed class StagedManifest
    {
        public List<string>? Instances { get; set; }
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
