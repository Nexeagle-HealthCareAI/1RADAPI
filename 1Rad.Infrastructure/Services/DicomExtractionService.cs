using System.IO.Compression;
using System.Text.Json;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Imaging.NativeCodec;
using FellowOakDicom.IO.Buffer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
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
    // Per-slice progressive preview: a tiny JPEG the viewer shows INSTANTLY while
    // the full HTJ2K slice streams (the "blurry → sharp" two-tier load). 128px
    // longest-edge ≈ 3-5 KB/slice — the low-bandwidth perceived-speed win.
    private const int PreviewMaxDim = 128;

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

    // Whether to ALSO write the raw .jhc progressive frame per slice. OFF by
    // default: the viewer currently loads the full .dcm (wadouri), and the
    // byte-range/wadors path that would CONSUME the .jhc isn't wired yet — so
    // producing frames just DOUBLES per-slice storage for no benefit. Storing
    // only the (already HTJ2K-compressed) .dcm realises the full ~2-3x saving.
    // Flip Dicom:WriteProgressiveFrames=true once the frontend wadors path ships.
    private readonly bool _writeFrames;

    // Whether to DELETE the original source ZIP after a fully clean extraction.
    // OFF by default. The retained ZIP is a re-extraction safety net but is pure
    // overhead once the per-slice HTJ2K blobs (which ARE the viewable study) are
    // committed — keeping it makes a 100 MB upload cost ~150-210 MB stored. With
    // this on, the footprint collapses to just the slices. Reclamation happens
    // ONLY after the slice index is durably saved AND only when no slice was
    // skipped, so a re-extract can never be left with neither source nor index.
    // Re-extracting a study whose source was reclaimed short-circuits to the
    // already-committed slices instead of failing. Dicom:DeleteSourceAfterExtraction.
    private readonly bool _deleteSourceAfterExtraction;

    // Write a tiny per-slice progressive preview JPEG (two-tier blurry→sharp
    // load). ON by default. Adds a small render + a few KB/slice; flip
    // Dicom:WriteSlicePreviews=false to skip it (e.g. to speed extraction).
    private readonly bool _writeSlicePreviews;

    // Max slices processed (transcode + parallel uploads) in flight at once.
    // Tunable now that the S3 client is a warm singleton and each slice fans its
    // .dcm/preview/thumbnail out concurrently — raise it to push more throughput
    // to the object store, lower it if the endpoint starts 503-ing. Bounds peak
    // memory too (~this many decoded slices held at once).
    private readonly int _sliceUploadConcurrency;

    // ── Server-side MPR reformatting (Dicom:WriteReformattedPlanes) ───────────
    // OFF by default. When ON, a reformat-eligible axial series ALSO gets
    // backend-generated CORONAL + SAGITTAL stacks, delivered as ordinary 2D
    // series (HTJ2K .dcm + previews). This brings proper coronal/sagittal to
    // LOW-BANDWIDTH + MOBILE users without a client-side volume — "coronal on
    // 3G" becomes just another fast 2D scroll.
    //
    // Memory safety (this path runs INSIDE the OOM-sensitive extraction): we
    // accumulate a DOWNSAMPLED volume (≤ ReformatMaxDim per in-plane axis) into
    // a single short[] buffer during the existing preview-decode pass — no second
    // decode, no re-download. A series whose downsampled volume would exceed
    // ReformatMaxVoxels is SKIPPED (client-side MPR remains the fallback), so a
    // huge study can never reintroduce the OOM. Only ONE series reformats at a
    // time (the largest eligible), bounding peak to a single buffer.
    private readonly bool _writeReformattedPlanes;
    private readonly int  _reformatMaxDim;     // cap in-plane resolution of the reformat volume
    private readonly long _reformatMaxVoxels;  // skip reformat if W'*H'*D exceeds this (memory guard)
    private readonly int  _reformatMinSlices;  // below this a series isn't worth reslicing

    public DicomExtractionService(
        IApplicationDbContext db,
        IBlobService blob,
        ILogger<DicomExtractionService> logger,
        IStudyMatchingService matching,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _db = db;
        _blob = blob;
        _logger = logger;
        _matching = matching;
        _writeFrames = configuration.GetValue("Dicom:WriteProgressiveFrames", false);
        // Default ON: once extraction succeeds cleanly the per-slice HTJ2K blobs
        // ARE the study, so the source ZIP is pure overhead. Set the flag to false
        // only to keep ZIPs (e.g. for debugging a problematic feed).
        _deleteSourceAfterExtraction = configuration.GetValue("Dicom:DeleteSourceAfterExtraction", true);
        _writeSlicePreviews = configuration.GetValue("Dicom:WriteSlicePreviews", true);
        _sliceUploadConcurrency = Math.Clamp(configuration.GetValue("Dicom:ExtractionSliceConcurrency", 6), 1, 32);
        _writeReformattedPlanes = configuration.GetValue("Dicom:WriteReformattedPlanes", false);
        // 256² in-plane keeps reformats triage-sharp while bounding memory + bytes
        // (these planes serve low-bandwidth/mobile, not primary full-res reads).
        _reformatMaxDim    = Math.Clamp(configuration.GetValue("Dicom:ReformatMaxDim", 256), 64, 512);
        // 40M voxels ≈ 80 MB at int16 — e.g. 256×256×610. Above this we skip
        // (client-side MPR remains) rather than risk the extraction OOM.
        _reformatMaxVoxels = configuration.GetValue("Dicom:ReformatMaxVoxels", 40_000_000L);
        _reformatMinSlices = Math.Max(configuration.GetValue("Dicom:ReformatMinSlices", 16), 8);

        // fo-dicom uses a manager pattern — wire the NATIVE codecs (JPEG-LS /
        // JPEG2000 / HTJ2K transcoders) once. Idempotent (second .Build() no-ops).
        // NOTE: we deliberately DON'T register an ImageManager — fo-dicom's
        // ImageSharp bridge (fo-dicom.Imaging.ImageSharp 5.1.2) is built against
        // ImageSharp 1.x and crashes on the 3.x we ship. Thumbnails/previews are
        // rendered directly from pixel data instead (see RenderGrayscaleJpeg).
        try
        {
            new DicomSetupBuilder()
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
    /// The raw HTJ2K progressive frame sits at the slice's `.dcm` blob path with
    /// a `.jhc` extension. Canonical derivation used by the delete/cleanup paths
    /// (so frame blobs are reclaimed with their slice) — must match the path the
    /// extraction loop writes. Returns null when the URL isn't a `.dcm`.
    /// </summary>
    public static string? FrameUrlFromSlice(string? sliceBlobUrl)
    {
        if (string.IsNullOrWhiteSpace(sliceBlobUrl)) return null;
        var q = sliceBlobUrl.IndexOf('?');
        var path = q >= 0 ? sliceBlobUrl[..q] : sliceBlobUrl;
        var query = q >= 0 ? sliceBlobUrl[q..] : string.Empty;
        if (!path.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase)) return null;
        return path[..^4] + ".jhc" + query;
    }

    /// <summary>
    /// The progressive-preview JPEG sits at the slice's `.dcm` path with a
    /// `_prev.jpg` suffix. Canonical derivation for delete/cleanup + the orphan
    /// sweep's referenced set. Returns null when the URL isn't a `.dcm`.
    /// </summary>
    public static string? PreviewUrlFromSlice(string? sliceBlobUrl)
    {
        if (string.IsNullOrWhiteSpace(sliceBlobUrl)) return null;
        var q = sliceBlobUrl.IndexOf('?');
        var path = q >= 0 ? sliceBlobUrl[..q] : sliceBlobUrl;
        var query = q >= 0 ? sliceBlobUrl[q..] : string.Empty;
        if (!path.EndsWith(".dcm", StringComparison.OrdinalIgnoreCase)) return null;
        return path[..^4] + "_prev.jpg" + query;
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

    // Upload a slice/frame/thumbnail with one retry. A transient blob hiccup on
    // ONE slice must not throw out of its task and fail the whole study.
    private async Task<string> UploadWithRetryAsync(byte[] bytes, string path, string contentType, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var ms = new MemoryStream(bytes, writable: false);
                return await _blob.UploadFileAtPathAsync(ms, path, contentType, Container, ImmutableCacheControl);
            }
            catch when (attempt < 1)
            {
                await Task.Delay(400, ct);
            }
        }
    }

    // Best-effort sibling of UploadWithRetryAsync for OPTIONAL artifacts
    // (frame / preview / thumbnail): swallows failures → null so one missing
    // extra never fails the slice, and — when a slice fires its uploads in
    // PARALLEL — never surfaces as an unobserved task fault if the critical
    // .dcm upload throws first. Cancellation still propagates (shutdown).
    private async Task<string?> SafeUploadAsync(byte[] bytes, string path, string contentType, CancellationToken ct, string label, Guid assetId)
    {
        try
        {
            return await UploadWithRetryAsync(bytes, path, contentType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DICOM_EXTRACT] Asset {AssetId} optional {Label} upload failed — continuing without.", assetId, label);
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
        asset.ExtractionPhase = "Downloading";
        asset.ExtractionProcessedSlices = 0;
        asset.ExtractionTotalSlices = 0;
        if (study != null) study.Status = ImagingStudyStatus.Processing;
        await _db.SaveChangesAsync(cancellationToken);

        // Wipe any prior slice index so re-runs are clean — AND delete the prior
        // run's blobs first, so a re-extraction that produces a different slice
        // layout doesn't strand the old slice/frame/thumbnail blobs. (The blob
        // orphan-sweep job is the backstop; this removes the orphan at source so
        // it usually never has to.)
        var prior = await _db.StudySliceIndexes
            .IgnoreQueryFilters()
            .Where(s => s.AssetId == assetId)
            .ToListAsync(cancellationToken);

        // Source-reclaimed guard: in storage-saving mode the original ZIP is
        // deleted after a clean extraction. A re-extract then has no source to
        // re-gather from — but the committed per-slice blobs already ARE the
        // study. Detect the missing source (size 0) with slices present and
        // short-circuit to Extracted/Ready, rather than wiping good slices below
        // and then failing the gather. (Only zip is ever reclaimed.)
        if (ext == "zip" && prior.Count > 0
            && await _blob.GetBlobSizeByUrlAsync(asset.BlobUrl) == 0)
        {
            _logger.LogInformation(
                "[DICOM_EXTRACT] Asset {AssetId}: source ZIP already reclaimed and {N} slices exist — treating as already extracted (no re-gather).",
                assetId, prior.Count);
            asset.ExtractionStatus      = "Extracted";
            asset.ExtractionSliceCount  = prior.Count;
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            asset.ExtractionError       = null;
            asset.ExtractionPhase       = null;
            asset.ExtractionLeaseOwner  = null;
            asset.ExtractionLeaseUntil  = null;
            if (study != null)
            {
                study.Status   = ImagingStudyStatus.Ready;
                study.ReadyAt ??= DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(cancellationToken);
            return prior.Count;
        }

        if (prior.Count > 0)
        {
            foreach (var s in prior)
            {
                if (string.IsNullOrWhiteSpace(s.BlobUrl)) continue;
                try { await _blob.DeleteFileAsync(s.BlobUrl, Container); } catch { /* best-effort */ }
                var frame = FrameUrlFromSlice(s.BlobUrl);
                if (frame != null) { try { await _blob.DeleteFileAsync(frame, Container); } catch { /* best-effort */ } }
                var preview = PreviewUrlFromSlice(s.BlobUrl);
                if (preview != null) { try { await _blob.DeleteFileAsync(preview, Container); } catch { /* best-effort */ } }
                if (!string.IsNullOrWhiteSpace(s.ThumbnailUrl))
                { try { await _blob.DeleteFileAsync(s.ThumbnailUrl, Container); } catch { /* best-effort */ } }
            }
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

            // Slice count is known now — publish the total so the viewer's bar
            // becomes determinate ("Processing N / Total"), and flip the phase.
            asset.ExtractionTotalSlices = parsedSlices.Count;
            asset.ExtractionPhase = "Processing";
            await _db.SaveChangesAsync(cancellationToken);

            // Live progress: a throttled background flush of the processed count to
            // the row, so ANY instance's status poll shows the bar advance without
            // a DB write per slice. Stopped before the EF phase below.
            var processedCount = 0;
            using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progressFlush = FlushProgressAsync(asset, () => Volatile.Read(ref processedCount), progressCts.Token);

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

            // Server-side MPR (opt-in, best-effort): pick the LARGEST series and
            // build a memory-bounded downsampled-volume accumulator, filled plane-
            // by-plane during the decode pass below. Only one accumulator exists at
            // a time, so peak added memory is a single (capped) short[] volume.
            ReformatAccumulator? reformatAcc = null;
            int reformatSeriesIndexBase = bySeries.Count; // coronal/sagittal series go after the axial ones
            if (_writeReformattedPlanes && bySeries.Count > 0)
            {
                var largest = bySeries.OrderByDescending(s => s.Slices.Count).First();
                reformatAcc = TryBuildReformatAccumulator(largest.Slices);
                if (reformatAcc != null)
                    _logger.LogInformation(
                        "[REFORMAT] Asset {AssetId}: eligible series ({D} slices) → reformat volume {W}×{H}×{D2} (factor {F}).",
                        assetId, largest.Slices.Count, reformatAcc.Width, reformatAcc.Height, reformatAcc.Depth, reformatAcc.Factor);
            }

            // Compression-stats accumulators — logged at end of extraction so
            // we can see the byte savings (and confirm transcoding actually ran).
            long totalOriginalBytes  = 0;
            long totalUploadedBytes  = 0;
            int  transcodedSliceCount = 0;
            int  failedSliceCount     = 0;

            // 4. Transcode + upload every slice IN PARALLEL (bounded), then
            // write the slice index sequentially. RESILIENCE: an individual
            // slice failing (transient blob error, etc.) must NOT fail the whole
            // study — each task catches its own error, retries the upload once,
            // and on final failure returns null instead of throwing. We deliver
            // the slices that DID succeed and only fail the extraction if none
            // did. (`series.Index` is carried as a plain int so the result tuple
            // is nameable/nullable.)
            using var uploadGate = new SemaphoreSlim(_sliceUploadConcurrency);
            var sliceTasks = bySeries
                .SelectMany(series => series.Slices.Select((p, i) => (seriesIndex: series.Index, p, i)))
                .Select(async item =>
                {
                    await uploadGate.WaitAsync(cancellationToken);
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (seriesIndex, p, i) = item;

                        // Re-open the DICOM HERE (not retained from gather) and dispose
                        // it at the end of this slice, so peak memory is bounded to the
                        // ~6 slices in flight rather than the whole study.
                        var originalBytes = p.OriginalBytes;
                        using var sliceMs = new MemoryStream(originalBytes, writable: false);
                        var dicom = DicomFile.Open(sliceMs, FileReadOption.ReadAll); // DicomFile is not IDisposable; GC reclaims it

                        // Transcode to HTJ2K Lossless RPCL — shrinks the payload and,
                        // crucially, makes each slice resolution-progressive (a low-res
                        // image decodes from a byte-range prefix). Lossless = primary-
                        // diagnosis safe; falls back to original bytes on any error.
                        var uploadBytes = TranscodeSliceBytes(dicom, originalBytes, out var didTranscode);
                        Interlocked.Add(ref totalOriginalBytes, originalBytes.Length);
                        Interlocked.Add(ref totalUploadedBytes, uploadBytes.Length);
                        if (didTranscode) Interlocked.Increment(ref transcodedSliceCount);

                        // Slice blob path: deterministic, easy to delete by prefix.
                        var sliceBlobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{seriesIndex:D3}/{i:D4}.dcm";

                        // C1: render the optional artifacts (CPU) up front, then upload
                        // the slice + frame + preview + thumbnail in PARALLEL. These were
                        // 2–4 SEQUENTIAL round-trips per slice; firing them together over
                        // the now-warm singleton connection cuts per-slice latency to
                        // roughly one round-trip.

                        // Raw HTJ2K frame for byte-range progressive loading (optional).
                        byte[]? frameBytes = null; string? frameBlobPath = null, frameContentType = null;
                        try
                        {
                            var fr = _writeFrames ? ExtractStreamableFrame(uploadBytes) : null;
                            if (fr.HasValue)
                            {
                                frameBytes = fr.Value.Frame;
                                frameBlobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{seriesIndex:D3}/{i:D4}.jhc";
                                frameContentType = $"application/octet-stream; transfer-syntax={fr.Value.TransferSyntaxUid}";
                                Interlocked.Add(ref totalUploadedBytes, frameBytes.Length);
                            }
                        }
                        catch (Exception frEx)
                        {
                            _logger.LogDebug(frEx, "[DICOM_EXTRACT] Asset {AssetId} frame {Idx}/{I} extract failed — progressive disabled for this slice.", assetId, seriesIndex, i);
                        }

                        // Per-slice progressive PREVIEW + series THUMBNAIL (first slice
                        // only) — both from ONE decode.
                        byte[]? prevBytes = null; string? prevPath = null;
                        byte[]? thumbBytes = null; string? thumbPath = null;
                        if (_writeSlicePreviews || i == 0)
                        {
                            try
                            {
                                if (_writeSlicePreviews)
                                {
                                    prevBytes = RenderGrayscaleJpeg(dicom, PreviewMaxDim, 55);
                                    if (prevBytes != null)
                                    {
                                        prevPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{seriesIndex:D3}/{i:D4}_prev.jpg";
                                        Interlocked.Add(ref totalUploadedBytes, prevBytes.Length);
                                    }
                                }
                                if (i == 0)
                                {
                                    thumbBytes = RenderGrayscaleJpeg(dicom, ThumbnailMaxDim, 70);
                                    if (thumbBytes != null)
                                        thumbPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/thumbs/{seriesIndex:D3}.jpg";
                                }
                            }
                            catch (Exception pvEx)
                            {
                                _logger.LogWarning(pvEx, "[DICOM_EXTRACT] Asset {AssetId} preview/thumb {Idx}/{I} render failed — continuing without.", assetId, seriesIndex, i);
                            }
                        }

                        // Fire all uploads concurrently. The .dcm is CRITICAL (its
                        // failure fails the slice → propagates); the rest are best-effort
                        // via SafeUploadAsync (failure → null, never an unobserved fault).
                        var sliceTask = UploadWithRetryAsync(uploadBytes, sliceBlobPath, "application/dicom", cancellationToken);
                        var frameTask = frameBytes != null
                            ? SafeUploadAsync(frameBytes, frameBlobPath!, frameContentType!, cancellationToken, "frame", assetId)
                            : Task.FromResult<string?>(null);
                        var previewTask = prevBytes != null
                            ? SafeUploadAsync(prevBytes, prevPath!, "image/jpeg", cancellationToken, "preview", assetId)
                            : Task.FromResult<string?>(null);
                        var thumbTask = thumbBytes != null
                            ? SafeUploadAsync(thumbBytes, thumbPath!, "image/jpeg", cancellationToken, "thumbnail", assetId)
                            : Task.FromResult<string?>(null);

                        // Server-side MPR plane fill (CPU) overlaps with the upload I/O.
                        // Its z-index is fixed by SopUid, so each task writes a DISTINCT
                        // plane — lock-free; a failure just leaves that plane zero.
                        if (reformatAcc != null && reformatAcc.ZBySop.TryGetValue(p.SopUid, out var rz))
                        {
                            if (FillReformatPlane(dicom, reformatAcc, rz))
                                Interlocked.Increment(ref reformatAcc.FilledPlanes);
                        }

                        var sliceUrl = await sliceTask;            // critical → propagates on failure
                        var frameUrl = await frameTask;
                        var previewUrl = await previewTask;
                        var thumbnailUrl = await thumbTask;

                        // Metadata JSON (while the DICOM is still open), carried in the
                        // result — the EF phase no longer needs the parsed file.
                        var metadataJson = ExtractMetadataJson(dicom, frameUrl, previewUrl);

                        // Release the raw bytes now that this slice is fully uploaded,
                        // so held memory shrinks as the study progresses.
                        p.OriginalBytes = Array.Empty<byte>();

                        return new SliceResult(seriesIndex, p, i, sliceUrl, sliceBlobPath, thumbnailUrl, frameUrl, metadataJson);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw; // shutdown — let it propagate to requeue
                    }
                    catch (Exception sliceEx)
                    {
                        Interlocked.Increment(ref failedSliceCount);
                        _logger.LogWarning(sliceEx, "[DICOM_EXTRACT] Asset {AssetId} slice {Series}/{I} failed after retry — skipping it.", assetId, item.seriesIndex, item.i);
                        return (SliceResult?)null;
                    }
                    finally
                    {
                        // Count every attempted slice (success OR skip) so the
                        // progress bar always reaches the total.
                        Interlocked.Increment(ref processedCount);
                        uploadGate.Release();
                    }
                })
                .ToList();

            var results = await Task.WhenAll(sliceTasks);

            // Stop the progress flusher BEFORE the EF phase resumes using _db.
            progressCts.Cancel();
            try { await progressFlush; } catch { /* ignore */ }

            var uploaded = results.Where(r => r.HasValue).Select(r => r!.Value).ToList();

            // Total failure (nothing landed) is a real failure → surface it.
            if (uploaded.Count == 0)
                throw new InvalidOperationException(
                    $"All {failedSliceCount} slice(s) failed to upload during extraction.");
            if (failedSliceCount > 0)
                _logger.LogWarning(
                    "[DICOM_EXTRACT] Asset {AssetId}: {Failed} slice(s) failed, {Ok} succeeded — delivering partial study.",
                    assetId, failedSliceCount, uploaded.Count);

            asset.ExtractionPhase = "Finalizing";

            // Sequential EF phase — deterministic order (series, instance).
            foreach (var u in uploaded.OrderBy(x => x.SeriesIndex).ThenBy(x => x.I))
            {
                _db.StudySliceIndexes.Add(new StudySliceIndex
                {
                    SliceId           = Guid.NewGuid(),
                    AssetId           = asset.Id,
                    AppointmentId     = asset.AppointmentId,
                    HospitalId        = asset.HospitalId,
                    SeriesUID         = u.P.SeriesUid,
                    SopInstanceUID    = u.P.SopUid,
                    InstanceNumber    = u.P.InstanceNumber,
                    SeriesDescription = Truncate(u.P.SeriesDescription, 200),
                    Modality          = Truncate(u.P.Modality, 16),
                    BlobUrl           = u.SliceUrl,
                    BlobPath          = u.SliceBlobPath,
                    ThumbnailUrl      = u.I == 0 ? u.ThumbnailUrl : null,
                    MetadataJson      = u.MetadataJson,
                    ExtractedAt       = DateTime.UtcNow,
                });
                sliceCount++;
            }

            asset.ExtractionStatus      = "Extracted";
            asset.ExtractionSliceCount  = sliceCount;
            asset.ExtractionCompletedAt = DateTime.UtcNow;
            asset.ExtractionError       = null;
            asset.ExtractionPhase       = null;
            asset.ExtractionProcessedSlices = sliceCount;
            asset.ExtractionLeaseOwner  = null;   // release the lease — job done
            asset.ExtractionLeaseUntil  = null;

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

            // Durable commit of the slice index + Extracted status BEFORE any
            // source deletion — so a crash can never leave a study with neither a
            // re-gatherable source nor a saved index.
            await _db.SaveChangesAsync(cancellationToken);

            // Server-side MPR (best-effort): axial is now durably committed AND
            // viewable, so reslice the in-memory volume into coronal + sagittal
            // series and add them. A failure here NEVER affects the axial study —
            // the user already has it; reformats simply won't appear this run.
            // `reformatBytes` is hoisted so the ZIP-reclaim metering below (which
            // RE-SETS StorageBytes) still accounts for the reformatted planes.
            long reformatBytes = 0;
            if (reformatAcc != null)
            {
                try
                {
                    asset.ExtractionPhase = "Reformatting";
                    await _db.SaveChangesAsync(cancellationToken);
                    var (rfRows, rfBytes) = await ReformatAndUploadAsync(
                        reformatAcc, asset, hospitalIdN, appointmentIdN, assetIdN, reformatSeriesIndexBase, cancellationToken);
                    if (rfRows > 0) { reformatBytes = rfBytes; asset.StorageBytes += rfBytes; }
                    asset.ExtractionPhase = null;
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception rfEx)
                {
                    _logger.LogWarning(rfEx, "[REFORMAT] Asset {AssetId}: reformatting failed — axial study unaffected.", assetId);
                    asset.ExtractionPhase = null;
                    try { await _db.SaveChangesAsync(cancellationToken); } catch { /* best-effort */ }
                }
                finally
                {
                    // Free the volume buffer promptly regardless of outcome.
                    reformatAcc = null;
                }
            }

            // Storage-saving (opt-in): the per-slice HTJ2K blobs are now committed
            // and ARE the viewable study, so the original ZIP is redundant. Reclaim
            // it — but only on a FULLY clean run (no skipped slices), so we never
            // drop the one recoverable source for a partial study. Best-effort: a
            // failed delete just leaves the ZIP for the orphan sweep / next run.
            if (ext == "zip" && _deleteSourceAfterExtraction && failedSliceCount == 0 && originalBlobBytes > 0)
            {
                try
                {
                    await _blob.DeleteFileAsync(asset.BlobUrl, Container);
                    asset.StorageBytes = totalUploadedBytes + reformatBytes;   // ZIP reclaimed — drop it from the meter (keep reformat planes)
                    await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation(
                        "[DICOM_EXTRACT] Asset {AssetId}: source ZIP reclaimed after clean extraction — freed ~{MB:N1} MB (footprint now {Now:N1} MB).",
                        assetId, originalBlobBytes / 1_048_576.0, (totalUploadedBytes + reformatBytes) / 1_048_576.0);
                }
                catch (Exception delEx)
                {
                    _logger.LogWarning(delEx,
                        "[DICOM_EXTRACT] Asset {AssetId}: source ZIP delete failed — keeping it (sweep can reclaim later).", assetId);
                }
            }

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
            asset.ExtractionPhase       = null;
            // The worker owns retry-vs-final-fail and clears the lease there.
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
        // The DICOM StudyDate tag (0008,0020) is authoritative for when the study
        // was acquired — backfill it when the registration request didn't supply
        // one (PACS-only uploads usually don't), so the studies list/sort isn't
        // blank. Only fill a null; never override an operator-provided date.
        if (first.StudyDate.HasValue && study.StudyDate == null) study.StudyDate = first.StudyDate;
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
            // ReadLargeOnDemand: parse the metadata WITHOUT pulling the pixel data
            // into managed memory — we only need the tags here. The pixels are
            // re-read from OriginalBytes by the processing task. Disposed at the
            // end of this method (the `using`), so nothing heavy is retained.
            var dicom = DicomFile.Open(ms, FileReadOption.ReadLargeOnDemand);
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
                StudyDate         = ParseDicomStudyDate(ds),
                AccessionNumber   = ds.GetSingleValueOrDefault<string?>(DicomTag.AccessionNumber, null),
                PatientName       = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientName, null),
                DicomPatientId    = ds.GetSingleValueOrDefault<string?>(DicomTag.PatientID, null),
                SeriesUid         = seriesUid,
                SopUid            = sopUid,
                InstanceNumber    = ds.GetSingleValueOrDefault<int?>(DicomTag.InstanceNumber, null),
                SeriesDescription = ds.GetSingleValueOrDefault<string?>(DicomTag.SeriesDescription, null),
                Modality          = ds.GetSingleValueOrDefault<string?>(DicomTag.Modality, null),
                Rows              = ds.GetSingleValueOrDefault<int?>(DicomTag.Rows, null),
                Columns           = ds.GetSingleValueOrDefault<int?>(DicomTag.Columns, null),
                PixelSpacing            = ReadDoubles(ds, DicomTag.PixelSpacing),
                ImageOrientationPatient = ReadDoubles(ds, DicomTag.ImageOrientationPatient),
                ImagePositionPatient    = ReadDoubles(ds, DicomTag.ImagePositionPatient),
                SliceThickness          = ds.GetSingleValueOrDefault<double?>(DicomTag.SliceThickness, null),
                WindowCenter            = ds.GetSingleValueOrDefault<double?>(DicomTag.WindowCenter, null),
                WindowWidth             = ds.GetSingleValueOrDefault<double?>(DicomTag.WindowWidth, null),
                FrameOfReferenceUid     = ds.GetSingleValueOrDefault<string?>(DicomTag.FrameOfReferenceUID, null),
                OriginalBytes     = bytes,
            };
        }
        catch (Exception parseEx)
        {
            _logger.LogDebug(parseEx, "[DICOM_EXTRACT] Asset {AssetId} {Label} not a valid DICOM file — skipping.", assetId, label);
            return null;
        }
    }

    // Read a multi-valued double tag (PixelSpacing / IOP / IPP) into an array,
    // or null when absent / malformed. Used to capture slice geometry cheaply
    // during the metadata-only parse for server-side reformatting.
    private static double[]? ReadDoubles(DicomDataset ds, DicomTag tag)
    {
        if (!ds.Contains(tag)) return null;
        try { var v = ds.GetValues<double>(tag); return v != null && v.Length > 0 ? v : null; }
        catch { return null; }
    }

    // DICOM StudyDate (0008,0020) is a DA value "YYYYMMDD"; StudyTime (0008,0030)
    // is a TM "HHMMSS.FFFFFF". Parse defensively (scanners emit odd punctuation /
    // partial times) and return null rather than throw on anything malformed.
    private static DateTime? ParseDicomStudyDate(DicomDataset ds)
    {
        try
        {
            var date = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyDate, null);
            if (string.IsNullOrWhiteSpace(date)) return null;
            date = date.Replace(".", string.Empty).Trim();          // tolerate "YYYY.MM.DD"
            if (date.Length < 8) return null;
            if (!DateTime.TryParseExact(date[..8], "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                return null;

            var time = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyTime, null)?.Trim();
            if (!string.IsNullOrEmpty(time))
            {
                int hh = time.Length >= 2 && int.TryParse(time[..2], out var h) ? h : 0;
                int mm = time.Length >= 4 && int.TryParse(time.Substring(2, 2), out var m) ? m : 0;
                int ss = time.Length >= 6 && int.TryParse(time.Substring(4, 2), out var s) ? s : 0;
                if (hh < 24 && mm < 60 && ss < 60) d = d.AddHours(hh).AddMinutes(mm).AddSeconds(ss);
            }
            return d;
        }
        catch { return null; }
    }

    // Throttled background flush of the live processed-slice count to the row so
    // the status poll (on ANY instance) shows the bar advance without a DB write
    // per slice. Runs only during the parallel slice phase, when _db is otherwise
    // idle (the main flow is parked on Task.WhenAll), so there is no concurrent
    // DbContext use; it's cancelled before the EF phase resumes.
    private async Task FlushProgressAsync(StudyAsset asset, Func<int> getProcessed, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                asset.ExtractionProcessedSlices = getProcessed();
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* extraction finished or shutting down */ }
        catch (Exception ex) { _logger.LogDebug(ex, "[DICOM_EXTRACT] progress flush failed (non-fatal)."); }
    }

    // ZIP path: stream the archive to a TEMP FILE (not a MemoryStream) so the
    // compressed bytes never sit in managed memory, then parse each entry. Only
    // metadata + the raw entry bytes are retained per slice; the parsed DicomFile
    // is NOT (see ParseSlice) — the processing task re-opens it on demand. The
    // temp file is always deleted.
    private async Task<List<ParsedSlice>> GatherSlicesFromZipAsync(StudyAsset asset, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"dicomzip_{Guid.NewGuid():N}.zip");
        try
        {
            await using (var dl = await _blob.DownloadFileAsync(asset.BlobUrl))
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                await dl.CopyToAsync(fs, ct);
            }

            using var zipFs = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            using var archive = new ZipArchive(zipFs, ZipArchiveMode.Read);
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
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort temp cleanup */ }
        }
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

    // Render a downsized grayscale JPEG (thumbnail / progressive preview) DIRECTLY
    // from the DICOM pixel data + the DICOM VOI window, using ImageSharp 3.x only
    // for resize + encode. This deliberately AVOIDS fo-dicom's
    // DicomImage.RenderImage()/AsSharpImage() bridge, which is binary-incompatible
    // with the ImageSharp 3.x we ship (fo-dicom.Imaging.ImageSharp 5.1.2 targets
    // ImageSharp 1.x) — that silent mismatch is why thumbnails/previews produced
    // nothing. Grayscale (MONOCHROME, SamplesPerPixel==1) only; colour → null.
    private byte[]? RenderGrayscaleJpeg(DicomFile file, int maxDim, int quality)
    {
        try
        {
            var ds = file.Dataset;
            // GetFrame needs uncompressed pixels — decompress an encapsulated
            // source first (the native codec, now loaded, handles this).
            if (ds.InternalTransferSyntax.IsEncapsulated)
                ds = new DicomTranscoder(ds.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian)
                        .Transcode(file).Dataset;

            var pd = DicomPixelData.Create(ds);
            int cols = pd.Width, rows = pd.Height;
            if (cols <= 0 || rows <= 0 || pd.SamplesPerPixel != 1) return null; // grayscale only

            var frame = pd.GetFrame(0).Data;
            int bits   = ds.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 16);
            bool signed = ds.GetSingleValueOrDefault<ushort>(DicomTag.PixelRepresentation, (ushort)0) == 1;
            double slope     = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
            double intercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
            bool mono1 = string.Equals(
                ds.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2"),
                "MONOCHROME1", StringComparison.OrdinalIgnoreCase);

            int n = cols * rows;
            int bytesPerPx = bits > 8 ? 2 : 1;
            if (frame.Length < n * bytesPerPx) return null;

            double ReadStored(int i) => bits > 8
                ? (signed ? BitConverter.ToInt16(frame, i * 2) : BitConverter.ToUInt16(frame, i * 2))
                : (signed ? (sbyte)frame[i] : frame[i]);

            double wc = ds.GetSingleValueOrDefault(DicomTag.WindowCenter, double.NaN);
            double ww = ds.GetSingleValueOrDefault(DicomTag.WindowWidth,  double.NaN);
            if (double.IsNaN(wc) || double.IsNaN(ww) || ww < 1)
            {
                // No window in the header — derive one from the data range.
                double mn = double.MaxValue, mx = double.MinValue;
                for (int i = 0; i < n; i++) { var v = ReadStored(i) * slope + intercept; if (v < mn) mn = v; if (v > mx) mx = v; }
                wc = (mn + mx) / 2.0; ww = Math.Max(1.0, mx - mn);
            }

            var gray = new byte[n];
            double lo = wc - 0.5 - (ww - 1) / 2.0;   // DICOM linear VOI LUT
            for (int i = 0; i < n; i++)
            {
                double v = ReadStored(i) * slope + intercept;
                int g = (int)Math.Round(Math.Clamp((v - lo) / (ww - 1), 0.0, 1.0) * 255);
                gray[i] = (byte)(mono1 ? 255 - g : g);
            }

            using var img = Image.LoadPixelData<L8>(gray, cols, rows);
            double sc = (double)maxDim / Math.Max(cols, rows);
            if (sc < 1.0) img.Mutate(x => x.Resize((int)(cols * sc), (int)(rows * sc)));
            using var ms = new MemoryStream();
            img.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOM_EXTRACT] grayscale preview/thumbnail render failed (continuing without).");
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // SERVER-SIDE MPR REFORMATTING (Dicom:WriteReformattedPlanes)
    //
    // Brings proper coronal/sagittal to LOW-BANDWIDTH + MOBILE users by reslicing
    // the axial volume into 2D stacks server-side, so the viewer never needs a
    // client volume. The whole path is BEST-EFFORT + memory-bounded: anything
    // failing logs + leaves the axial study untouched, and a study whose
    // downsampled volume would exceed the voxel cap is skipped entirely (the
    // client-side MPR overlay remains the fallback). The pure reslice + geometry
    // math lives in VolumeReformatter (unit-tested); this part is the decode →
    // accumulate → encode → upload plumbing.
    // ══════════════════════════════════════════════════════════════════════════

    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    // Per-series accumulator: a single downsampled short[] volume filled plane-by-
    // plane during the existing decode pass (each axial slice writes its own z),
    // plus the geometry needed to reslice it. Bounded to ReformatMaxVoxels.
    private sealed class ReformatAccumulator
    {
        public int SrcWidth, SrcHeight;            // source cols/rows (each slice must match)
        public int Width, Height, Depth;           // downsampled W', H', and slice count D
        public int Factor;                         // integer box-downsample factor
        public short[] Voxels = System.Array.Empty<short>();
        public int FilledPlanes;                   // Interlocked — coverage check
        public Dictionary<string, int> ZBySop = new(); // SopUid → z-plane index
        public double[] RowDir = { 1, 0, 0 }, ColDir = { 0, 1, 0 }, SliceDir = { 0, 0, 1 };
        public double ColSpacing = 1, RowSpacing = 1, SliceSpacing = 1; // dx', dy', dz (mm)
        public double[] Origin = { 0, 0, 0 };
        public string StudyUid = string.Empty;
        public string? FrameOfReferenceUid;
        public string? Modality;
        public double? WindowCenter, WindowWidth;
    }

    // Decide eligibility + build the accumulator for ONE series (the largest), or
    // null to skip. Caps memory: a volume above ReformatMaxVoxels is rejected.
    private ReformatAccumulator? TryBuildReformatAccumulator(List<ParsedSlice> slices)
    {
        try
        {
            if (slices.Count < _reformatMinSlices) return null;
            var f = slices[0];
            if (f.Rows is not int rows || f.Columns is not int cols || rows <= 0 || cols <= 0) return null;
            if (f.PixelSpacing is not { Length: >= 2 } ps) return null;
            if (f.ImageOrientationPatient is not { Length: >= 6 } iop) return null;

            int factor = Math.Max(1, (int)Math.Ceiling(Math.Max(cols, rows) / (double)_reformatMaxDim));
            int W = Math.Max(1, cols / factor), H = Math.Max(1, rows / factor), D = slices.Count;
            if ((long)W * H * D > _reformatMaxVoxels)
            {
                _logger.LogInformation("[REFORMAT] series skipped — {V} voxels exceeds cap {Cap}; client-side MPR remains.", (long)W * H * D, _reformatMaxVoxels);
                return null;
            }

            var rowDir = new[] { iop[0], iop[1], iop[2] };
            var colDir = new[] { iop[3], iop[4], iop[5] };
            var sliceDir = VolumeReformatter.Normalize(VolumeReformatter.Cross(rowDir, colDir));

            // z-order: by IPP projected on the slice normal when every slice has a
            // position (robust to InstanceNumber quirks); else InstanceNumber order.
            bool haveIpp = slices.All(s => s.ImagePositionPatient is { Length: >= 3 });
            var ordered = haveIpp
                ? slices.OrderBy(s => Dot(s.ImagePositionPatient!, sliceDir)).ToList()
                : slices;

            var zBySop = new Dictionary<string, int>(D);
            for (int z = 0; z < ordered.Count; z++)
                if (!string.IsNullOrEmpty(ordered[z].SopUid)) zBySop[ordered[z].SopUid] = z;

            double dz;
            if (haveIpp && ordered.Count > 1)
            {
                var a = ordered[0].ImagePositionPatient!; var b = ordered[^1].ImagePositionPatient!;
                double span = Math.Abs(Dot(new[] { b[0] - a[0], b[1] - a[1], b[2] - a[2] }, sliceDir));
                dz = span > 1e-6 ? span / (ordered.Count - 1) : (f.SliceThickness ?? 1.0);
            }
            else dz = f.SliceThickness ?? 1.0;
            if (dz <= 0) dz = 1.0;

            var origin = ordered[0].ImagePositionPatient is { Length: >= 3 } o
                ? new[] { o[0], o[1], o[2] } : new[] { 0.0, 0, 0 };

            return new ReformatAccumulator
            {
                SrcWidth = cols, SrcHeight = rows, Width = W, Height = H, Depth = D, Factor = factor,
                Voxels = new short[(long)W * H * D],
                ZBySop = zBySop, RowDir = rowDir, ColDir = colDir, SliceDir = sliceDir,
                ColSpacing = ps[1] * factor, RowSpacing = ps[0] * factor, SliceSpacing = dz,
                Origin = origin, StudyUid = f.StudyUid ?? string.Empty, Modality = f.Modality,
                WindowCenter = f.WindowCenter, WindowWidth = f.WindowWidth,
                FrameOfReferenceUid = f.FrameOfReferenceUid,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[REFORMAT] accumulator build failed — skipping reformat for this study.");
            return null;
        }
    }

    // Decode one axial slice's pixels (rescaled to intensity) and box-downsample
    // them into the accumulator's z-plane. Returns false (leaving the plane zero)
    // on any mismatch/error — never throws into the slice task.
    private bool FillReformatPlane(DicomFile file, ReformatAccumulator acc, int z)
    {
        try
        {
            var ds = file.Dataset;
            if (ds.InternalTransferSyntax.IsEncapsulated)
                ds = new DicomTranscoder(ds.InternalTransferSyntax, DicomTransferSyntax.ExplicitVRLittleEndian).Transcode(file).Dataset;

            var pd = DicomPixelData.Create(ds);
            int cols = pd.Width, srows = pd.Height;
            if (cols != acc.SrcWidth || srows != acc.SrcHeight || pd.SamplesPerPixel != 1) return false;

            var frame = pd.GetFrame(0).Data;
            int bits = ds.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 16);
            bool signed = ds.GetSingleValueOrDefault<ushort>(DicomTag.PixelRepresentation, (ushort)0) == 1;
            double slope = ds.GetSingleValueOrDefault(DicomTag.RescaleSlope, 1.0);
            double intercept = ds.GetSingleValueOrDefault(DicomTag.RescaleIntercept, 0.0);
            int bpp = bits > 8 ? 2 : 1;
            if (frame.Length < cols * srows * bpp) return false;

            double ReadStored(int i) => bits > 8
                ? (signed ? BitConverter.ToInt16(frame, i * 2) : BitConverter.ToUInt16(frame, i * 2))
                : (signed ? (sbyte)frame[i] : frame[i]);

            int W = acc.Width, H = acc.Height, fac = acc.Factor;
            long planeBase = (long)z * W * H;
            for (int oy = 0; oy < H; oy++)
            {
                int sy0 = oy * fac, sy1 = Math.Min(sy0 + fac, srows);
                for (int ox = 0; ox < W; ox++)
                {
                    int sx0 = ox * fac, sx1 = Math.Min(sx0 + fac, cols);
                    double sum = 0; int cnt = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int rowBase = sy * cols;
                        for (int sx = sx0; sx < sx1; sx++) { sum += ReadStored(rowBase + sx) * slope + intercept; cnt++; }
                    }
                    double avg = cnt > 0 ? sum / cnt : 0;
                    acc.Voxels[planeBase + oy * W + ox] = (short)Math.Clamp(Math.Round(avg), short.MinValue, short.MaxValue);
                }
            }
            return true;
        }
        catch { return false; }
    }

    // Reslice the accumulated volume into coronal + sagittal series and upload
    // each plane as an HTJ2K .dcm (+ preview, + first-plane thumbnail), adding a
    // StudySliceIndex row per plane. Returns (rows added, bytes uploaded).
    private async Task<(int rows, long bytes)> ReformatAndUploadAsync(
        ReformatAccumulator acc, StudyAsset asset,
        string hospitalIdN, string appointmentIdN, string assetIdN,
        int firstNewSeriesIndex, CancellationToken ct)
    {
        // Require meaningful coverage — a half-empty volume reslices to garbage.
        if (acc.FilledPlanes < acc.Depth * 0.6)
        {
            _logger.LogWarning("[REFORMAT] only {Filled}/{Depth} planes filled — skipping reslice (insufficient coverage).", acc.FilledPlanes, acc.Depth);
            return (0, 0);
        }

        var vol = new VolumeReformatter.AxialVolume
        {
            Voxels = acc.Voxels, Width = acc.Width, Height = acc.Height, Depth = acc.Depth,
            RowDir = acc.RowDir, ColDir = acc.ColDir, SliceDir = acc.SliceDir,
            ColSpacing = acc.ColSpacing, RowSpacing = acc.RowSpacing, SliceSpacing = acc.SliceSpacing,
            Origin = acc.Origin,
        };

        int rows = 0; long bytes = 0;
        var orientations = new (VolumeReformatter.Plane plane, string desc, int seriesIndex)[]
        {
            (VolumeReformatter.Plane.Coronal,  "CORONAL (MPR)",  firstNewSeriesIndex),
            (VolumeReformatter.Plane.Sagittal, "SAGITTAL (MPR)", firstNewSeriesIndex + 1),
        };

        foreach (var (plane, desc, seriesIndex) in orientations)
        {
            var seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            int seriesNumber = 9000 + seriesIndex; // clearly distinct from acquisition series
            int planeNo = 0;
            foreach (var rp in VolumeReformatter.Reslice(vol, plane, _reformatMaxDim))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                    var dcmBytes = BuildReformatDicomBytes(rp, acc, sopUid, seriesUid, seriesNumber, desc, out var builtFile);
                    var blobPath = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{seriesIndex:D3}/{rp.PlaneIndex:D4}.dcm";
                    var url = await UploadWithRetryAsync(dcmBytes, blobPath, "application/dicom", ct);
                    bytes += dcmBytes.Length;

                    string? previewUrl = null, thumbnailUrl = null;
                    try
                    {
                        var prev = RenderGrayscaleJpeg(builtFile, PreviewMaxDim, 55);
                        if (prev != null)
                        {
                            var pp = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/series/{seriesIndex:D3}/{rp.PlaneIndex:D4}_prev.jpg";
                            previewUrl = await UploadWithRetryAsync(prev, pp, "image/jpeg", ct);
                            bytes += prev.Length;
                        }
                        if (planeNo == 0)
                        {
                            var thumb = RenderGrayscaleJpeg(builtFile, ThumbnailMaxDim, 70);
                            if (thumb != null)
                            {
                                var tp = $"{hospitalIdN}/{appointmentIdN}/extracted/{assetIdN}/thumbs/{seriesIndex:D3}.jpg";
                                thumbnailUrl = await UploadWithRetryAsync(thumb, tp, "image/jpeg", ct);
                                bytes += thumb.Length;
                            }
                        }
                    }
                    catch (Exception pvEx) { _logger.LogDebug(pvEx, "[REFORMAT] preview/thumb render failed for {Desc} plane {P}.", desc, rp.PlaneIndex); }

                    var metadataJson = ExtractMetadataJson(builtFile, null, previewUrl);
                    _db.StudySliceIndexes.Add(new StudySliceIndex
                    {
                        SliceId = Guid.NewGuid(),
                        AssetId = asset.Id,
                        AppointmentId = asset.AppointmentId,
                        HospitalId = asset.HospitalId,
                        SeriesUID = seriesUid,
                        SopInstanceUID = sopUid,
                        InstanceNumber = rp.PlaneIndex + 1,
                        SeriesDescription = desc,
                        Modality = Truncate(acc.Modality, 16),
                        BlobUrl = url,
                        BlobPath = blobPath,
                        ThumbnailUrl = planeNo == 0 ? thumbnailUrl : null,
                        MetadataJson = metadataJson,
                        ExtractedAt = DateTime.UtcNow,
                    });
                    rows++; planeNo++;
                }
                catch (Exception ex) { _logger.LogDebug(ex, "[REFORMAT] {Desc} plane {P} failed — skipping.", desc, rp.PlaneIndex); }
            }
            _logger.LogInformation("[REFORMAT] {Desc}: {N} planes uploaded.", desc, planeNo);
        }
        return (rows, bytes);
    }

    // Construct a single-frame 16-bit MONOCHROME2 DICOM from a reformatted plane
    // (+ its geometry), then transcode to HTJ2K Lossless RPCL for storage. The
    // out param is the UNCOMPRESSED file so the caller renders the preview without
    // a second decode. Pixel values are rescaled intensity (slope=1, intercept=0).
    private byte[] BuildReformatDicomBytes(
        VolumeReformatter.ReformatPlane rp, ReformatAccumulator acc,
        string sopUid, string seriesUid, int seriesNumber, string seriesDesc, out DicomFile builtFile)
    {
        var ds = new DicomDataset(DicomTransferSyntax.ExplicitVRLittleEndian);
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.SecondaryCaptureImageStorage);
        ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
        if (!string.IsNullOrEmpty(acc.StudyUid)) ds.AddOrUpdate(DicomTag.StudyInstanceUID, acc.StudyUid);
        ds.AddOrUpdate(DicomTag.SeriesInstanceUID, seriesUid);
        if (!string.IsNullOrEmpty(acc.FrameOfReferenceUid)) ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, acc.FrameOfReferenceUid);
        ds.AddOrUpdate(DicomTag.Modality, string.IsNullOrEmpty(acc.Modality) ? "OT" : acc.Modality);
        ds.AddOrUpdate(DicomTag.SeriesDescription, seriesDesc);
        ds.AddOrUpdate(DicomTag.SeriesNumber, seriesNumber);
        ds.AddOrUpdate(DicomTag.InstanceNumber, rp.PlaneIndex + 1);
        ds.AddOrUpdate(DicomTag.ImageType, "DERIVED", "SECONDARY", "REFORMATTED");
        ds.AddOrUpdate(DicomTag.Rows, (ushort)rp.Height);
        ds.AddOrUpdate(DicomTag.Columns, (ushort)rp.Width);
        ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
        ds.AddOrUpdate(DicomTag.BitsStored, (ushort)16);
        ds.AddOrUpdate(DicomTag.HighBit, (ushort)15);
        ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)1); // signed (rescaled intensity)
        ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
        ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
        ds.AddOrUpdate(DicomTag.PixelSpacing, rp.RowSpacing, rp.ColSpacing);
        ds.AddOrUpdate(DicomTag.ImageOrientationPatient, rp.ImageOrientationPatient);
        ds.AddOrUpdate(DicomTag.ImagePositionPatient, rp.ImagePositionPatient);
        ds.AddOrUpdate(DicomTag.RescaleSlope, 1.0);
        ds.AddOrUpdate(DicomTag.RescaleIntercept, 0.0);
        if (acc.WindowCenter is double wc) ds.AddOrUpdate(DicomTag.WindowCenter, wc);
        if (acc.WindowWidth is double ww) ds.AddOrUpdate(DicomTag.WindowWidth, ww);

        var raw = new byte[rp.Pixels.Length * 2];
        Buffer.BlockCopy(rp.Pixels, 0, raw, 0, raw.Length);
        var px = DicomPixelData.Create(ds, true);
        px.AddFrame(new MemoryByteBuffer(raw));

        builtFile = new DicomFile(ds); // uncompressed — preview renders directly from this
        try
        {
            var ht = new DicomTranscoder(DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.HTJ2KLosslessRPCL).Transcode(builtFile);
            using var ms = new MemoryStream();
            ht.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[REFORMAT] HTJ2K transcode of reformat plane failed — storing uncompressed.");
            using var ms = new MemoryStream();
            builtFile.Save(ms);
            return ms.ToArray();
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
    /// `frameUrl` (when present) points the wadors loader at the raw HTJ2K frame;
    /// `previewUrl` points the viewer at the tiny progressive preview JPEG.
    /// </summary>
    private static string? ExtractMetadataJson(DicomFile file, string? frameUrl = null, string? previewUrl = null)
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
            // Tiny progressive-preview JPEG (blurry→sharp two-tier load), CDN-
            // rewritten by the manifest builder like frameUrl.
            if (!string.IsNullOrWhiteSpace(previewUrl)) dict["previewUrl"] = previewUrl;

            return dict.Count == 0 ? null : JsonSerializer.Serialize(dict);
        }
        catch
        {
            return null;
        }
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));

    // One successfully-uploaded slice's outputs, carried from the parallel
    // upload phase to the sequential EF-write phase. A value type so the task
    // can return SliceResult? (null = this slice failed and was skipped).
    private readonly record struct SliceResult(
        int SeriesIndex, ParsedSlice P, int I,
        string SliceUrl, string SliceBlobPath, string? ThumbnailUrl, string? FrameUrl,
        string? MetadataJson);

    private sealed class ParsedSlice
    {
        public string EntryName         { get; set; } = string.Empty;
        public string? StudyUid         { get; set; }
        public string? StudyDescription { get; set; }
        public DateTime? StudyDate      { get; set; }
        public string? AccessionNumber  { get; set; }
        public string? PatientName      { get; set; }
        public string? DicomPatientId   { get; set; }
        public string SeriesUid         { get; set; } = string.Empty;
        public string SopUid            { get; set; } = string.Empty;
        public int? InstanceNumber      { get; set; }
        public string? SeriesDescription { get; set; }
        public string? Modality         { get; set; }
        // Geometry (read cheaply during the metadata-only parse) — used to decide
        // reformat eligibility (uniform spacing / consistent orientation) and to
        // compute the coronal/sagittal plane geometry. All optional; a series
        // missing them simply isn't server-side reformatted.
        public int? Rows                { get; set; }
        public int? Columns             { get; set; }
        public double[]? PixelSpacing            { get; set; } // [rowSpacing(Δy), colSpacing(Δx)]
        public double[]? ImageOrientationPatient { get; set; } // [rowDir(3), colDir(3)]
        public double[]? ImagePositionPatient    { get; set; } // [x,y,z] of voxel (0,0)
        public double? SliceThickness            { get; set; }
        public double? WindowCenter              { get; set; }
        public double? WindowWidth               { get; set; }
        public string? FrameOfReferenceUid       { get; set; }
        // Raw bytes only — the parsed DicomFile is NOT retained (it holds the
        // decoded pixel data, ~doubling RAM per slice). The processing task
        // re-opens it on demand and disposes it, so peak memory is bounded to the
        // few slices in flight rather than the WHOLE study at once. This is the
        // fix for OOM-on-large-studies under concurrency.
        public byte[] OriginalBytes     { get; set; } = Array.Empty<byte>();
    }
}
