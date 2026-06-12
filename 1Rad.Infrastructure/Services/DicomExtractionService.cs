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
    private readonly IStudyMatchingService _matching;

    public DicomExtractionService(
        IApplicationDbContext db,
        IBlobService blob,
        ILogger<DicomExtractionService> logger,
        IStudyMatchingService matching)
    {
        _db = db;
        _blob = blob;
        _logger = logger;
        _matching = matching;

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

    /// <summary>
    /// Extracts the raw HTJ2K codestream (frame 0) + its transfer-syntax UID from
    /// a transcoded DICOM byte buffer, for byte-range progressive delivery. The
    /// viewer's wadors loader can ONLY do partial/range decode on the streamable
    /// HTJ2K syntaxes (RPCL .202 and HTJ2K .203); .201 (non-RPCL) and anything
    /// non-HTJ2K are skipped (those slices fall back to whole-file .dcm loading).
    /// Returns null when no streamable single frame is available.
    /// </summary>
    private (byte[] Frame, string TransferSyntaxUid)? ExtractStreamableFrame(byte[] dcmBytes)
    {
        try
        {
            using var ms = new MemoryStream(dcmBytes, writable: false);
            var f = DicomFile.Open(ms);
            var ts = f.Dataset.InternalTransferSyntax;
            var streamable = ts == DicomTransferSyntax.HTJ2KLosslessRPCL // .202
                          || ts == DicomTransferSyntax.HTJ2K;            // .203
            if (!streamable) return null;

            var pd = FellowOakDicom.Imaging.DicomPixelData.Create(f.Dataset);
            if (pd.NumberOfFrames < 1) return null;
            var frame = pd.GetFrame(0).Data; // raw encapsulated codestream bytes
            if (frame == null || frame.Length == 0) return null;
            return (frame, ts.UID.UID);
        }
        catch
        {
            // Any parse/extract failure → no progressive frame; .dcm fallback covers it.
            return null;
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

        // The imaging aggregate this asset feeds (Phase 1 of the RIS/PACS
        // split). Null only for legacy rows uploaded before the backfill ran.
        var study = asset.ImagingStudyId != null
            ? await _db.ImagingStudies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(st => st.Id == asset.ImagingStudyId, cancellationToken)
            : null;

        // ZIP archives, per-instance ("instances") uploads AND single .dcm
        // files all get extracted into per-slice blobs. Single DCMs go through
        // the same pipeline so they are normalised (fo-dicom tolerates files
        // saved without the 128-byte preamble/DICM marker; the transcoded
        // output is always a valid P10 the browser parser accepts) and gain
        // HTJ2K compression + a thumbnail. Non-DICOM attachments (jpg/png/pdf)
        // load directly in the viewer.
        var ext = (asset.FileType ?? "").Trim().ToLowerInvariant();
        if (ext != "zip" && ext != "instances" && ext != "dcm" && ext != "dicom")
        {
            asset.ExtractionStatus = "NotApplicable";
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            if (study != null && study.Status != ImagingStudyStatus.Ready)
            {
                study.Status = ImagingStudyStatus.Ready;   // directly viewable
                study.ReadyAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        asset.ExtractionStatus = "Running";
        asset.ExtractionStartedAt = DateTime.UtcNow;
        asset.ExtractionError = null;
        if (study != null) study.Status = ImagingStudyStatus.Processing;
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
            // 1+2. Gather DICOM slices from the source — a ZIP archive, a set
            // of pre-staged per-instance blobs (the bridge's SAS-per-file
            // path), or a single uploaded .dcm. All yield a flat list of
            // parsed slices; everything downstream (grouping, transcode,
            // index, thumbnails) is identical.
            var parsedSlices = ext switch
            {
                "instances" => await GatherSlicesFromStagedInstancesAsync(asset, cancellationToken),
                "zip"       => await GatherSlicesFromZipAsync(asset, cancellationToken),
                _           => await GatherSlicesFromSingleDcmAsync(asset, cancellationToken),
            };

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
            // PACS-only assets have no appointment — scope their blobs by the
            // study id instead (legacy appointment-scoped paths are unchanged).
            var appointmentIdN = (asset.AppointmentId ?? asset.ImagingStudyId ?? asset.Id).ToString("N");
            var sliceCount     = 0;

            // Compression-stats accumulators — logged at end of extraction so
            // we can see the byte savings (and confirm transcoding actually ran).
            long totalOriginalBytes  = 0;
            long totalUploadedBytes  = 0;
            int  transcodedSliceCount = 0;

            // 4. Transcode + upload every slice IN PARALLEL (bounded), then
            // write the slice index sequentially. The previous one-at-a-time
            // loop serialised CPU transcode behind network upload RTT — a
            // 300-slice study took minutes. Transcode is CPU-bound, upload is
            // IO-bound; they pipeline well at modest concurrency. The
            // DbContext is NOT thread-safe, so all EF writes happen after the
            // parallel phase completes (re-runs wipe the index anyway, so the
            // old chunked-save crash recovery is unnecessary).
            using var uploadGate = new SemaphoreSlim(6);
            var sliceTasks = bySeries
                .SelectMany(series => series.Slices.Select((p, i) => (series, p, i)))
                .Select(async item =>
                {
                    await uploadGate.WaitAsync(cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (series, p, i) = item;

                        // Transcode to HTJ2K Lossless RPCL — shrinks the payload and,
                        // crucially, makes each slice resolution-progressive (a low-res
                        // image decodes from a byte-range prefix). Lossless = primary-
                        // diagnosis safe; falls back to original bytes on any error.
                        var uploadBytes = TranscodeSliceBytes(p.DicomFile, p.OriginalBytes, out var didTranscode);
                        Interlocked.Add(ref totalOriginalBytes, p.OriginalBytes.Length);
                        Interlocked.Add(ref totalUploadedBytes, uploadBytes.Length);
                        if (didTranscode) Interlocked.Increment(ref transcodedSliceCount);

                        // Slice blob path: deterministic, easy to delete by prefix.
                        var sliceBlobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{series.Index:D3}/{i:D4}.dcm";
                        string sliceUrl;
                        using (var sliceUp = new MemoryStream(uploadBytes, writable: false))
                        {
                            sliceUrl = await _blob.UploadFileAtPathAsync(sliceUp, sliceBlobPath, "application/dicom", Container, ImmutableCacheControl);
                        }

                        // Raw HTJ2K frame, served alongside the .dcm for byte-range
                        // progressive loading (the viewer's wadors path range-reads
                        // this and paints low-res-first). Content-type carries the
                        // transfer syntax so Cornerstone treats it as streamable
                        // HTJ2K. Best-effort: a slice without a streamable frame just
                        // falls back to whole-file .dcm loading.
                        string? frameUrl = null;
                        long frameBytesLen = 0;
                        try
                        {
                            var fr = ExtractStreamableFrame(uploadBytes);
                            if (fr.HasValue)
                            {
                                var frameBlobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{series.Index:D3}/{i:D4}.jhc";
                                var frameContentType = $"application/octet-stream; transfer-syntax={fr.Value.TransferSyntaxUid}";
                                using var frameUp = new MemoryStream(fr.Value.Frame, writable: false);
                                frameUrl = await _blob.UploadFileAtPathAsync(frameUp, frameBlobPath, frameContentType, Container, ImmutableCacheControl);
                                frameBytesLen = fr.Value.Frame.Length;
                                Interlocked.Add(ref totalUploadedBytes, frameBytesLen);
                            }
                        }
                        catch (Exception frEx)
                        {
                            _logger.LogDebug(frEx, "[DICOM_EXTRACT] Asset {AssetId} frame {Idx}/{I} write failed — progressive disabled for this slice.", assetId, item.series.Index, item.i);
                        }

                        // Thumbnail: first slice of each series only.
                        string? thumbnailUrl = null;
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
                                _logger.LogDebug(thumbEx, "[DICOM_EXTRACT] Asset {AssetId} series {Idx} thumbnail failed — continuing without.", assetId, item.series.Index);
                            }
                        }

                        return (item.series, item.p, item.i, sliceUrl, sliceBlobPath, thumbnailUrl, frameUrl);
                    }
                    finally
                    {
                        uploadGate.Release();
                    }
                })
                .ToList();

            var uploaded = await Task.WhenAll(sliceTasks);

            // Sequential EF phase — deterministic order (series, instance).
            foreach (var u in uploaded.OrderBy(x => x.series.Index).ThenBy(x => x.i))
            {
                _db.StudySliceIndexes.Add(new StudySliceIndex
                {
                    SliceId           = Guid.NewGuid(),
                    AssetId           = asset.Id,
                    AppointmentId     = asset.AppointmentId,
                    HospitalId        = asset.HospitalId,
                    SeriesUID         = u.p.SeriesUid,
                    SopInstanceUID    = u.p.SopUid,
                    InstanceNumber    = u.p.InstanceNumber,
                    SeriesDescription = Truncate(u.p.SeriesDescription, 200),
                    Modality          = Truncate(u.p.Modality, 16),
                    BlobUrl           = u.sliceUrl,
                    BlobPath          = u.sliceBlobPath,
                    ThumbnailUrl      = u.i == 0 ? u.thumbnailUrl : null,
                    MetadataJson      = ExtractMetadataJson(u.p.DicomFile, u.frameUrl),
                    ExtractedAt       = DateTime.UtcNow,
                });
                sliceCount++;
            }

            asset.ExtractionStatus      = "Extracted";
            asset.ExtractionSliceCount  = sliceCount;
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            asset.ExtractionError       = null;

            // Storage metering (Phase 3): the asset's durable footprint is the
            // retained original blob (ZIPs are kept; per-instance staging gets
            // swept) plus the transcoded slices. Recomputed in full, so
            // re-extractions don't double-count.
            var originalBlobBytes = ext == "zip"
                ? await _blob.GetBlobSizeByUrlAsync(asset.BlobUrl)
                : 0L;
            asset.StorageBytes = originalBlobBytes + totalUploadedBytes;

            if (study != null)
                await RefineStudyFromDicomAsync(study, parsedSlices[0], cancellationToken);

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
            if (study != null) study.Status = ImagingStudyStatus.Failed;
            await _db.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(ex, "[DICOM_EXTRACT] Asset {AssetId} extraction failed.", assetId);
            throw;
        }
    }

    /// <summary>
    /// Refine the ImagingStudy aggregate with the REAL DICOM tags once
    /// extraction has parsed the pixels (until now the row only carried what
    /// the appointment knew). Sets StudyInstanceUID under the per-hospital
    /// unique constraint: if another study in this hospital already owns the
    /// UID (same study uploaded twice against different appointments), the
    /// UID is left null and a warning logged — merging duplicates is the
    /// Phase 2 Upload Center's job, and a null UID only means "no dedup",
    /// never a broken viewer.
    /// </summary>
    private async Task RefineStudyFromDicomAsync(ImagingStudy study, ParsedSlice first, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(first.StudyUid) && string.IsNullOrEmpty(study.StudyInstanceUID))
        {
            var uidTaken = await _db.ImagingStudies.IgnoreQueryFilters().AnyAsync(
                st => st.HospitalId == study.HospitalId
                      && st.StudyInstanceUID == first.StudyUid
                      && st.Id != study.Id, ct);
            if (uidTaken)
                _logger.LogWarning(
                    "[DICOM_EXTRACT] Study {StudyId}: StudyInstanceUID {Uid} already exists for this hospital — leaving UID null (duplicate upload?).",
                    study.Id, first.StudyUid);
            else
                study.StudyInstanceUID = Truncate(first.StudyUid, 128);
        }

        if (!string.IsNullOrEmpty(first.StudyDescription)) study.StudyDescription = Truncate(first.StudyDescription, 255);
        if (!string.IsNullOrEmpty(first.AccessionNumber))  study.AccessionNumber  = Truncate(first.AccessionNumber, 64);
        if (!string.IsNullOrEmpty(first.Modality))         study.Modality         = Truncate(first.Modality, 32);
        // Demographics: prefer what the modality wrote over the appointment
        // denorm only when present — DICOM is authoritative for the pixels.
        if (!string.IsNullOrEmpty(first.PatientName))      study.PatientName      = Truncate(first.PatientName, 255);
        if (!string.IsNullOrEmpty(first.DicomPatientId))   study.DicomPatientId   = Truncate(first.DicomPatientId, 128);

        study.Status  = ImagingStudyStatus.Ready;
        study.ReadyAt = DateTime.UtcNow;

        // PACS-only studies arrive with no appointment; now that the real DICOM
        // identifiers (accession / patient id / name) are known, try to
        // reconcile them to a patient/visit. Appointment-linked (RIS+PACS)
        // studies are already assigned — skip. The caller owns SaveChanges.
        if (study.AppointmentId == null)
            await _matching.TryMatchAsync(study, ct);
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
                StudyUid          = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyInstanceUID, null),
                StudyDescription  = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyDescription, null),
                AccessionNumber   = ds.GetSingleValueOrDefault<string?>(DicomTag.AccessionNumber, null),
                PatientName       = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientName, null),
                DicomPatientId    = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientID, null),
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

    // Single-DCM path: the asset blob IS the one instance. Normalised through
    // the same transcode pipeline so even preamble-less files come out as
    // valid HTJ2K P10 slices.
    private async Task<List<ParsedSlice>> GatherSlicesFromSingleDcmAsync(StudyAsset asset, CancellationToken ct)
    {
        using var stream = await _blob.DownloadFileAsync(asset.BlobUrl);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var slice = ParseSlice(asset.Id, asset.FileName ?? "instance.dcm", ms.ToArray());
        return slice != null ? new List<ParsedSlice> { slice } : new List<ParsedSlice>();
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
        // Download + parse in parallel (bounded) — the sequential loop paid a
        // full round-trip per instance, which alone took minutes on large
        // studies. Order is restored downstream by the series/instance sort.
        using var downloadGate = new SemaphoreSlim(8);
        var downloadTasks = urls.Select(async url =>
        {
            await downloadGate.WaitAsync(ct);
            try
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
                    return null;
                }
                return ParseSlice(asset.Id, url, bytes);
            }
            finally
            {
                downloadGate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(downloadTasks);
        var parsed = results.Where(s => s != null).Select(s => s!).ToList();

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
    ///
    /// Carries the FULL pixel module (rows/columns/samplesPerPixel/bitsAllocated/
    /// bitsStored/highBit/pixelRepresentation/planarConfiguration/photometric)
    /// plus plane geometry (IOP/IPP/pixelSpacing) and VOI/modality LUT, because
    /// the byte-range progressive (wadors) path has no .dcm header to parse — the
    /// frontend builds a Cornerstone metadata provider from exactly these fields.
    /// `frameUrl` (when present) points the wadors loader at the raw HTJ2K frame.
    /// </summary>
    private static string? ExtractMetadataJson(DicomFile file, string? frameUrl = null)
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
            void AddDoubles(string key, DicomTag tag)
            {
                if (!ds.Contains(tag)) return;
                try { var vals = ds.GetValues<double>(tag); if (vals?.Length > 0) dict[key] = vals; }
                catch { /* malformed multi-value — skip */ }
            }

            // VOI / modality LUT
            Add<double>("windowCenter", DicomTag.WindowCenter);
            Add<double>("windowWidth",  DicomTag.WindowWidth);
            Add<double>("rescaleSlope",     DicomTag.RescaleSlope);
            Add<double>("rescaleIntercept", DicomTag.RescaleIntercept);
            // Pixel module (required by the wadors metadata provider)
            Add<int>("rows",                 DicomTag.Rows);
            Add<int>("columns",              DicomTag.Columns);
            Add<int>("samplesPerPixel",      DicomTag.SamplesPerPixel);
            Add<int>("bitsAllocated",        DicomTag.BitsAllocated);
            Add<int>("bitsStored",           DicomTag.BitsStored);
            Add<int>("highBit",              DicomTag.HighBit);
            Add<int>("pixelRepresentation",  DicomTag.PixelRepresentation);
            Add<int>("planarConfiguration",  DicomTag.PlanarConfiguration);
            AddStr("photometricInterpretation", DicomTag.PhotometricInterpretation);
            // Plane geometry (measurements / MPR)
            Add<double>("sliceLocation",    DicomTag.SliceLocation);
            Add<double>("sliceThickness",   DicomTag.SliceThickness);
            AddStr("patientPosition",       DicomTag.PatientPosition);
            AddDoubles("pixelSpacing",          DicomTag.PixelSpacing);
            AddDoubles("imageOrientationPatient", DicomTag.ImageOrientationPatient);
            AddDoubles("imagePositionPatient",    DicomTag.ImagePositionPatient);

            // Raw HTJ2K frame for byte-range progressive delivery (blob URL,
            // rewritten to the CDN host by the manifest builder's ToCdn()).
            if (!string.IsNullOrWhiteSpace(frameUrl)) dict["frameUrl"] = frameUrl;

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
        public string? StudyUid         { get; set; }
        public string? StudyDescription { get; set; }
        public string? AccessionNumber  { get; set; }
        public string? PatientName      { get; set; }
        public string? DicomPatientId   { get; set; }
        public string SeriesUid         { get; set; } = string.Empty;
        public string SopUid            { get; set; } = string.Empty;
        public int? InstanceNumber      { get; set; }
        public string? SeriesDescription { get; set; }
        public string? Modality         { get; set; }
        public DicomFile DicomFile      { get; set; } = null!;
        public byte[] OriginalBytes     { get; set; } = Array.Empty<byte>();
    }
}
