using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FellowOakDicom;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Interop;

/// <summary>
/// STOW-RS (PS3.18 §10.5): instances sent in over DICOMweb go into the same
/// pipeline as the bridge's per-instance uploads — staged blobs, then the durable
/// extraction queue (HTJ2K transcode, slice index, thumbnails, study matching).
///
/// Batching: modalities and PACS send a study as many small requests (Orthanc
/// sends ~10 instances per request). Each study gets ONE open "stow" asset whose
/// extraction is held back for <c>DicomWeb:StowBatchSeconds</c> (default 20 s)
/// after the latest request, so a 500-slice CT becomes one extraction, not fifty.
/// The asset's manifest names its staging folder rather than a file list, so
/// parallel requests only add files; nothing is rewritten.
/// </summary>
public sealed class DicomStowService
{
    private const string Container = "dicom-files";

    // PS3.18 Table 10.5.3-1 / PS3.4 C.4.2.1.4 failure reasons.
    public const int FailureCannotUnderstand = 0xC000;     // 49152
    public const int FailureStudyUidMismatch = 0xA900;     // 43264
    public const int FailureOutOfResources = 0xA700;       // 42752
    public const int FailureProcessing = 0x0110;           // 272

    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly IDicomExtractionQueue _queue;
    private readonly IStudyMatchingService _matching;
    private readonly IStorageMeteringService _storage;
    private readonly ILogger<DicomStowService> _logger;
    private readonly int _batchSeconds;

    public DicomStowService(
        IApplicationDbContext db,
        IBlobService blob,
        IDicomExtractionQueue queue,
        IStudyMatchingService matching,
        IStorageMeteringService storage,
        IConfiguration configuration,
        ILogger<DicomStowService> logger)
    {
        _db = db;
        _blob = blob;
        _queue = queue;
        _matching = matching;
        _storage = storage;
        _logger = logger;
        _batchSeconds = Math.Clamp(configuration.GetValue("DicomWeb:StowBatchSeconds", 20), 5, 300);
    }

    public sealed record StowResult(int StatusCode, JsonObject Body, int Stored, int Failed);

    private sealed record Parsed(byte[] Bytes, string SopClass, string SopUid, string StudyUid, DicomDataset Header);

    private sealed record Failure(string? SopClass, string? SopUid, int Reason);

    /// <summary>
    /// Store the parts. <paramref name="routeStudyUid"/> is set for
    /// POST /studies/{study}: instances of any other study are refused.
    /// </summary>
    public async Task<StowResult> StoreAsync(
        IReadOnlyList<byte[]> parts, string? routeStudyUid, Guid hospitalId, string baseUrl, CancellationToken ct)
    {
        var failures = new List<Failure>();
        var stored = new List<(Parsed P, string Url)>();

        // 1. Parse every part (header only — pixels stay as bytes).
        var parsed = new List<Parsed>();
        foreach (var bytes in parts)
        {
            var p = TryParse(bytes);
            if (p == null) { failures.Add(new Failure(null, null, FailureCannotUnderstand)); continue; }
            if (routeStudyUid != null && p.StudyUid != routeStudyUid)
            {
                failures.Add(new Failure(p.SopClass, p.SopUid, FailureStudyUidMismatch));
                continue;
            }
            parsed.Add(p);
        }

        // 2. Storage quota — same rule as every other upload path.
        if (parsed.Count > 0 && await _storage.IsOverQuotaAsync(hospitalId, ct))
        {
            failures.AddRange(parsed.Select(p => new Failure(p.SopClass, p.SopUid, FailureOutOfResources)));
            parsed.Clear();
        }

        // 3. Per study: find or create the study, add to its open batch, upload.
        foreach (var group in parsed.GroupBy(p => p.StudyUid))
        {
            try
            {
                var study = await FindOrCreateStudyAsync(group.Key, group.First().Header, hospitalId, ct);
                var result = await StageAsync(study, group.ToList(), ct);
                stored.AddRange(result.Stored);
                failures.AddRange(result.Failed.Select(p => new Failure(p.SopClass, p.SopUid, FailureProcessing)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[STOW] study {StudyUid} failed", group.Key);
                failures.AddRange(group.Select(p => new Failure(p.SopClass, p.SopUid, FailureProcessing)));
            }
        }

        if (stored.Count > 0) _storage.Invalidate(hospitalId);

        // 4. Response (PS3.18 §10.5.3): 200 all stored, 202 some failed, 409 none stored.
        var body = new JsonObject();
        var studyUids = stored.Select(s => s.P.StudyUid).Distinct().ToList();
        if (studyUids.Count == 1)
            body.Str("00081190", "UR", $"{baseUrl}/studies/{studyUids[0]}");
        if (failures.Count > 0)
            body.Seq("00081198", failures.Select(f => DicomJsonBuilder.New()
                .Str("00081150", "UI", f.SopClass)
                .Str("00081155", "UI", f.SopUid)
                .Int("00081197", "US", f.Reason)));
        if (stored.Count > 0)
            body.Seq("00081199", stored.Select(s => DicomJsonBuilder.New()
                .Str("00081150", "UI", s.P.SopClass)
                .Str("00081155", "UI", s.P.SopUid)
                .Str("00081190", "UR", $"{baseUrl}/studies/{s.P.StudyUid}/series/{s.P.Header.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty)}/instances/{s.P.SopUid}")));

        var status = stored.Count == 0 ? 409 : failures.Count > 0 ? 202 : 200;
        return new StowResult(status, body, stored.Count, failures.Count);
    }

    private Parsed? TryParse(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var file = DicomFile.Open(ms, FileReadOption.SkipLargeTags);
            var ds = file.Dataset;
            var sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
            var sopClass = ds.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
            var studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
            var seriesUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
            if (sopUid.Length == 0 || studyUid.Length == 0 || seriesUid.Length == 0) return null;
            if (studyUid.Length > 64 || sopUid.Length > 64) return null;
            return new Parsed(bytes, sopClass, sopUid, studyUid, ds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[STOW] part is not a readable DICOM file");
            return null;
        }
    }

    private async Task<ImagingStudy> FindOrCreateStudyAsync(string studyUid, DicomDataset first, Guid hospitalId, CancellationToken ct)
    {
        var existing = await _db.ImagingStudies.FirstOrDefaultAsync(s => s.HospitalId == hospitalId && s.StudyInstanceUID == studyUid, ct);
        if (existing != null) return existing;

        var study = new ImagingStudy
        {
            Id = Guid.NewGuid(),
            HospitalId = hospitalId,
            StudyInstanceUID = studyUid,
            PatientName = Trim(first.GetSingleValueOrDefault<string?>(DicomTag.PatientName, null), 200),
            DicomPatientId = Trim(first.GetSingleValueOrDefault<string?>(DicomTag.PatientID, null), 64),
            AccessionNumber = Trim(first.GetSingleValueOrDefault<string?>(DicomTag.AccessionNumber, null), 64),
            Modality = Trim(first.GetSingleValueOrDefault<string?>(DicomTag.Modality, null), 16),
            StudyDescription = Trim(first.GetSingleValueOrDefault<string?>(DicomTag.StudyDescription, null), 200),
            StudyDate = ReadStudyDate(first),
            Source = "dicomweb-stow",
            Status = ImagingStudyStatus.Received,
            MatchStatus = ImagingStudyMatchStatus.Unmatched,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ImagingStudies.Add(study);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another request created the same study a moment ago (unique UID per centre).
            if (_db is DbContext ctx) ctx.Entry(study).State = EntityState.Detached;
            var again = await _db.ImagingStudies.FirstOrDefaultAsync(s => s.HospitalId == hospitalId && s.StudyInstanceUID == studyUid, ct);
            if (again != null) return again;
            throw;
        }

        // Link to the patient / appointment (worklist UID, accession, MRN, unique name).
        await _matching.TryMatchAsync(study, ct);
        await _db.SaveChangesAsync(ct);
        return study;
    }

    private sealed record StageResult(List<(Parsed P, string Url)> Stored, List<Parsed> Failed);

    private async Task<StageResult> StageAsync(ImagingStudy study, List<Parsed> instances, CancellationToken ct)
    {
        var assetId = await OpenBatchAsync(study, ct);
        var prefix = StagingPrefix(study, assetId);

        var stored = new List<(Parsed, string)>();
        var failed = new List<Parsed>();
        long bytes = 0;
        foreach (var p in instances)
        {
            try
            {
                // Named by the SOP Instance UID: the same instance sent twice overwrites itself.
                var path = $"{prefix}{Sha1(p.SopUid)}.dcm";
                using var ms = new MemoryStream(p.Bytes, writable: false);
                var url = await _blob.UploadFileAtPathAsync(ms, path, "application/dicom", Container);
                stored.Add((p, url));
                bytes += p.Bytes.LongLength;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[STOW] upload failed for {Sop}", p.SopUid);
                failed.Add(p);
            }
        }
        if (stored.Count == 0) return new StageResult(stored, failed);

        // The batch must still be waiting; if extraction picked it up while we were
        // uploading, queue what we just added as its own job so nothing is missed
        // (a duplicate is harmless — DICOMweb serves each SOP Instance UID once).
        if (!await ExtendBatchAsync(assetId, ct))
        {
            var followUp = await CreateAssetAsync(study, manifestJson: null, explicitUrls: stored.Select(s => s.Item2).ToList(), ct);
            assetId = followUp;
        }

        await _db.StudyAssets.Where(a => a.Id == assetId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.StorageBytes, a => a.StorageBytes + bytes), ct);
        _queue.Enqueue(assetId);
        return new StageResult(stored, failed);
    }

    private static string StagingPrefix(ImagingStudy study, Guid assetId) =>
        $"{study.HospitalId:N}/{study.Id:N}/staging/{assetId:N}/";

    /// <summary>The study's waiting batch (extended), or a new one.</summary>
    private async Task<Guid> OpenBatchAsync(ImagingStudy study, CancellationToken ct)
    {
        var margin = DateTime.UtcNow.AddSeconds(5);
        var candidates = await _db.StudyAssets.AsNoTracking()
            .Where(a => a.ImagingStudyId == study.Id
                        && a.FileType == "instances"
                        && a.FileName == StowFileName
                        && a.ExtractionStatus == "Queued"
                        && a.ExtractionLeaseOwner == null
                        && a.ExtractionNextAttemptAt > margin)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => a.Id)
            .Take(1)
            .ToListAsync(ct);
        if (candidates.Count == 1 && await ExtendBatchAsync(candidates[0], ct))
            return candidates[0];

        var newId = Guid.NewGuid();
        var manifest = $"{{\"Prefix\":\"{StagingPrefix(study, newId)}\"}}";
        return await CreateAssetAsync(study, manifest, null, ct, newId);
    }

    private const string StowFileName = "dicomweb_stow";

    /// <summary>Push the batch's start time out again — only while no worker has it.</summary>
    private async Task<bool> ExtendBatchAsync(Guid assetId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var margin = now.AddSeconds(5);
        var until = now.AddSeconds(_batchSeconds);
        var n = await _db.StudyAssets
            .Where(a => a.Id == assetId
                        && a.ExtractionStatus == "Queued"
                        && a.ExtractionLeaseOwner == null
                        && a.ExtractionNextAttemptAt > margin)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExtractionNextAttemptAt, until), ct);
        return n == 1;
    }

    private async Task<Guid> CreateAssetAsync(
        ImagingStudy study, string? manifestJson, List<string>? explicitUrls, CancellationToken ct, Guid? id = null)
    {
        var assetId = id ?? Guid.NewGuid();
        manifestJson ??= System.Text.Json.JsonSerializer.Serialize(new { Instances = explicitUrls ?? new List<string>() });
        var manifestPath = $"{StagingPrefix(study, assetId)}_manifest.json";
        string manifestUrl;
        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson)))
            manifestUrl = await _blob.UploadFileAtPathAsync(ms, manifestPath, "application/json", Container);

        _db.StudyAssets.Add(new StudyAsset
        {
            Id = assetId,
            ImagingStudyId = study.Id,
            AppointmentId = study.AppointmentId,
            AppointmentServiceId = study.AppointmentServiceId,
            BlobUrl = manifestUrl,
            FileName = StowFileName,
            FileType = "instances",
            TechnicianComments = string.Empty,
            UploadedAt = DateTime.UtcNow,
            HospitalId = study.HospitalId,
            ExtractionStatus = "Queued",
            ExtractionNextAttemptAt = DateTime.UtcNow.AddSeconds(_batchSeconds),
        });
        await _db.SaveChangesAsync(ct);
        return assetId;
    }

    private static DateTime? ReadStudyDate(DicomDataset ds)
    {
        var d = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyDate, null);
        if (string.IsNullOrWhiteSpace(d)) return null;
        if (!DateTime.TryParseExact(d.Trim(), "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date)) return null;
        var t = ds.GetSingleValueOrDefault<string?>(DicomTag.StudyTime, null);
        if (!string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 4
            && int.TryParse(t.Trim().Substring(0, 2), out var hh) && int.TryParse(t.Trim().Substring(2, 2), out var mm)
            && hh is >= 0 and < 24 && mm is >= 0 and < 60)
            date = date.AddHours(hh).AddMinutes(mm);
        return date;
    }

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s.Substring(0, max) : s;
    }

    private static string Sha1(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
