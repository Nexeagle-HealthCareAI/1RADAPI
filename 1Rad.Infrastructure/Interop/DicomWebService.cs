using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace _1Rad.Infrastructure.Interop;

/// <summary>One retrievable instance: a row of the slice index plus its study.</summary>
public sealed record DicomWebInstance(
    Guid SliceId,
    Guid StudyId,
    Guid HospitalId,
    string StudyUid,
    string SeriesUid,
    string SopUid,
    int? InstanceNumber,
    string BlobUrl,
    string? MetadataJson,
    string? SeriesDescription,
    string? Modality,
    DateTime ExtractedAt);

/// <summary>A frame (or instance) ready to write as one multipart part.</summary>
public sealed record DicomWebPart(byte[] Bytes, string ContentType, string? Location = null);

/// <summary>
/// DICOMweb read side (QIDO-RS search, WADO-RS retrieve) over what 1Rad already
/// stores: <see cref="ImagingStudy"/> rows, the per-slice index
/// (<see cref="StudySliceIndex"/>) and the extracted .dcm blobs.
///
/// Rules:
///   • Only studies that are Ready, have a Study Instance UID and at least one
///     extracted instance are visible — everything listed can be downloaded.
///   • Server-made MPR planes ("… (MPR)" series) are hidden; outside viewers
///     build their own reformats from the acquired images.
///   • The same SOP Instance UID uploaded twice is served once (latest copy).
///   • Everything is scoped to the caller's centre by the global tenant filter.
/// </summary>
public sealed class DicomWebService
{
    private const string Container = "dicom-files";
    private const string MprSuffix = "(MPR)";
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(15);

    private readonly IApplicationDbContext _db;
    private readonly IBlobService _blob;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DicomWebService> _logger;

    public DicomWebService(IApplicationDbContext db, IBlobService blob, IMemoryCache cache, ILogger<DicomWebService> logger)
    {
        _db = db;
        _blob = blob;
        _cache = cache;
        _logger = logger;
        DicomCodecs.Ensure();
    }

    // ── Base queries ────────────────────────────────────────────────────────

    private IQueryable<ImagingStudy> VisibleStudies() =>
        _db.ImagingStudies.AsNoTracking()
            .Where(s => s.StudyInstanceUID != null
                        && s.Status == ImagingStudyStatus.Ready
                        && _db.StudySliceIndexes.Any(sl => sl.Asset.ImagingStudyId == s.Id));

    private IQueryable<StudySliceIndex> AcquiredSlices() =>
        _db.StudySliceIndexes.AsNoTracking()
            .Where(sl => sl.Asset.ImagingStudyId != null
                         && (sl.SeriesDescription == null || !sl.SeriesDescription.EndsWith(MprSuffix)));

    // ── QIDO-RS: studies ────────────────────────────────────────────────────

    public async Task<JsonArray> SearchStudiesAsync(QidoQuery q, string baseUrl, CancellationToken ct)
    {
        var result = new JsonArray();
        if (q.InvalidDate) return result;

        var studies = ApplyStudyFilters(VisibleStudies(), q);

        var page = await studies
            .OrderByDescending(s => s.StudyDate)
            .ThenByDescending(s => s.CreatedAt)
            .Skip(q.Offset)
            .Take(q.Limit)
            .Select(s => new
            {
                s.Id,
                Uid = s.StudyInstanceUID!,
                s.PatientName,
                s.DicomPatientId,
                s.AccessionNumber,
                s.Modality,
                s.StudyDate,
                s.StudyDescription,
                PatientGender = s.Patient != null ? s.Patient.Gender : null,
                PatientMrn = s.Patient != null ? s.Patient.PatientIdentifier : null,
                PatientFullName = s.Patient != null ? s.Patient.FullName : null,
                ReferredBy = s.Appointment != null ? s.Appointment.ReferredBy : null,
            })
            .ToListAsync(ct);
        if (page.Count == 0) return result;

        var ids = page.Select(p => p.Id).ToList();
        var stats = await StudyStatsAsync(ids, ct);

        foreach (var s in page)
        {
            stats.TryGetValue(s.Id, out var st);
            var o = DicomJsonBuilder.New()
                .Str("00080005", "CS", "ISO_IR 192")
                .Date("00080020", s.StudyDate)
                .Time("00080030", s.StudyDate is { TimeOfDay.Ticks: > 0 } ? s.StudyDate : null)
                .Str("00080050", "SH", DicomJsonBuilder.Clean(s.AccessionNumber, 16))
                .Strs("00080061", "CS", st?.Modalities ?? new List<string?> { s.Modality })
                .Str("00081030", "LO", DicomJsonBuilder.Clean(s.StudyDescription, 64))
                .Str("00081190", "UR", $"{baseUrl}/studies/{s.Uid}")
                .Pn("00100010", DicomJsonBuilder.Clean(s.PatientName ?? s.PatientFullName, 64))
                .Str("00100020", "LO", DicomJsonBuilder.Clean(s.DicomPatientId ?? s.PatientMrn, 64))
                .Str("00100040", "CS", DicomJsonBuilder.Sex(s.PatientGender))
                .Str("0020000D", "UI", s.Uid)
                .Str("00200010", "SH", DicomJsonBuilder.Clean(s.AccessionNumber, 16))
                .Int("00201206", "IS", st?.Series ?? 0)
                .Int("00201208", "IS", st?.Instances ?? 0);
            if (!o.ContainsKey("00080090"))
            {
                var referrer = DicomJsonBuilder.Clean(s.ReferredBy, 64);
                if (referrer != null) o.Pn("00080090", referrer); else o.Empty("00080090", "PN");
            }
            if (!o.ContainsKey("00100030")) o.Empty("00100030", "DA");
            result.Add(o);
        }
        return result;
    }

    private IQueryable<ImagingStudy> ApplyStudyFilters(IQueryable<ImagingStudy> studies, QidoQuery q)
    {
        if (q.StudyInstanceUids.Count > 0)
            studies = studies.Where(s => q.StudyInstanceUids.Contains(s.StudyInstanceUID!));

        if (!string.IsNullOrWhiteSpace(q.PatientName))
        {
            if (q.Fuzzy)
            {
                foreach (var token in q.PatientName.Split(new[] { ' ', '^', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = QidoQuery.ToLikePattern(token, contains: true);
                    if (p == null) continue;
                    studies = studies.Where(s => s.PatientName != null && EF.Functions.Like(s.PatientName.Replace("^", " "), p));
                }
            }
            else
            {
                var p = QidoQuery.ToLikePattern(q.PatientName, contains: false);
                if (p != null)
                    studies = studies.Where(s => s.PatientName != null && EF.Functions.Like(s.PatientName.Replace("^", " "), p));
            }
        }
        if (!string.IsNullOrWhiteSpace(q.PatientId))
        {
            var p = QidoQuery.ToLikePattern(q.PatientId, contains: false);
            if (p != null)
                studies = studies.Where(s =>
                    (s.DicomPatientId != null && EF.Functions.Like(s.DicomPatientId, p))
                    || (s.Patient != null && s.Patient.PatientIdentifier != null && EF.Functions.Like(s.Patient.PatientIdentifier, p)));
        }
        if (!string.IsNullOrWhiteSpace(q.AccessionNumber))
        {
            var p = QidoQuery.ToLikePattern(q.AccessionNumber, contains: false);
            if (p != null) studies = studies.Where(s => s.AccessionNumber != null && EF.Functions.Like(s.AccessionNumber, p));
        }
        if (!string.IsNullOrWhiteSpace(q.StudyDescription))
        {
            var p = QidoQuery.ToLikePattern(q.StudyDescription, contains: q.Fuzzy);
            if (p != null) studies = studies.Where(s => s.StudyDescription != null && EF.Functions.Like(s.StudyDescription, p));
        }
        if (!string.IsNullOrWhiteSpace(q.Modality))
        {
            var m = q.Modality.Trim();
            studies = studies.Where(s => s.Modality == m
                || _db.StudySliceIndexes.Any(sl => sl.Asset.ImagingStudyId == s.Id && sl.Modality == m));
        }
        if (q.StudyDateFrom != null)
        {
            var from = q.StudyDateFrom.Value;
            studies = studies.Where(s => s.StudyDate != null && s.StudyDate >= from);
        }
        if (q.StudyDateToExclusive != null)
        {
            var to = q.StudyDateToExclusive.Value;
            studies = studies.Where(s => s.StudyDate != null && s.StudyDate < to);
        }
        return studies;
    }

    private sealed record StudyStats(int Series, int Instances, List<string?> Modalities);

    private async Task<Dictionary<Guid, StudyStats>> StudyStatsAsync(List<Guid> studyIds, CancellationToken ct)
    {
        var counts = await AcquiredSlices()
            .Where(sl => studyIds.Contains(sl.Asset.ImagingStudyId!.Value))
            .GroupBy(sl => sl.Asset.ImagingStudyId!.Value)
            .Select(g => new
            {
                StudyId = g.Key,
                Series = g.Select(x => x.SeriesUID).Distinct().Count(),
                Instances = g.Select(x => x.SopInstanceUID).Distinct().Count(),
            })
            .ToListAsync(ct);

        var modalities = await AcquiredSlices()
            .Where(sl => studyIds.Contains(sl.Asset.ImagingStudyId!.Value) && sl.Modality != null)
            .Select(sl => new { StudyId = sl.Asset.ImagingStudyId!.Value, sl.Modality })
            .Distinct()
            .ToListAsync(ct);

        var mods = modalities.GroupBy(m => m.StudyId)
            .ToDictionary(g => g.Key, g => g.Select(x => (string?)x.Modality).ToList());

        return counts.ToDictionary(
            c => c.StudyId,
            c => new StudyStats(c.Series, c.Instances, mods.TryGetValue(c.StudyId, out var l) ? l : new List<string?>()));
    }

    // ── QIDO-RS: series ─────────────────────────────────────────────────────

    /// <summary>Series of one study (studyUid set) or across studies. Null when the study is unknown.</summary>
    public async Task<JsonArray?> SearchSeriesAsync(string? studyUid, QidoQuery q, string baseUrl, CancellationToken ct)
    {
        var studies = VisibleStudies();
        if (studyUid != null)
        {
            studies = studies.Where(s => s.StudyInstanceUID == studyUid);
            if (!await studies.AnyAsync(ct)) return null;
        }
        else
        {
            if (q.InvalidDate) return new JsonArray();
            studies = ApplyStudyFilters(studies, q);
        }

        var studyRows = await studies.Select(s => new { s.Id, Uid = s.StudyInstanceUID! }).Take(5000).ToListAsync(ct);
        var studyIds = studyRows.Select(s => s.Id).ToList();
        var uidOf = studyRows.ToDictionary(s => s.Id, s => s.Uid);

        var slices = AcquiredSlices().Where(sl => studyIds.Contains(sl.Asset.ImagingStudyId!.Value));
        if (q.SeriesInstanceUids.Count > 0) slices = slices.Where(sl => q.SeriesInstanceUids.Contains(sl.SeriesUID));
        if (studyUid != null && !string.IsNullOrWhiteSpace(q.Modality))
        {
            var m = q.Modality.Trim();
            slices = slices.Where(sl => sl.Modality == m);
        }
        if (!string.IsNullOrWhiteSpace(q.SeriesDescription))
        {
            var p = QidoQuery.ToLikePattern(q.SeriesDescription, contains: q.Fuzzy);
            if (p != null) slices = slices.Where(sl => sl.SeriesDescription != null && EF.Functions.Like(sl.SeriesDescription, p));
        }

        var series = await slices
            .GroupBy(sl => new { StudyId = sl.Asset.ImagingStudyId!.Value, sl.SeriesUID })
            .Select(g => new
            {
                g.Key.StudyId,
                g.Key.SeriesUID,
                Modality = g.Max(x => x.Modality),
                Description = g.Max(x => x.SeriesDescription),
                Instances = g.Select(x => x.SopInstanceUID).Distinct().Count(),
                MinInstance = g.Min(x => x.InstanceNumber),
            })
            .OrderBy(x => x.StudyId).ThenBy(x => x.MinInstance).ThenBy(x => x.SeriesUID)
            .Skip(q.Offset).Take(q.Limit)
            .ToListAsync(ct);

        // Series number lives in the slice's MetadataJson (written by extraction
        // since Oct 2026). Read the metadata of the page's series and keep the
        // first non-empty value per series.
        var pageSeries = series.Select(s => s.SeriesUID).Distinct().ToList();
        var metaRows = await AcquiredSlices()
            .Where(sl => studyIds.Contains(sl.Asset.ImagingStudyId!.Value)
                         && pageSeries.Contains(sl.SeriesUID)
                         && sl.MetadataJson != null)
            .Select(sl => new { StudyId = sl.Asset.ImagingStudyId!.Value, sl.SeriesUID, sl.InstanceNumber, sl.MetadataJson })
            .ToListAsync(ct);
        var meta = metaRows
            .GroupBy(r => (r.StudyId, r.SeriesUID))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.InstanceNumber ?? int.MaxValue).First().MetadataJson);

        var result = new JsonArray();
        foreach (var s in series)
        {
            meta.TryGetValue((s.StudyId, s.SeriesUID), out var mj);
            var info = SliceMeta.Read(mj);
            var stUid = uidOf[s.StudyId];
            result.Add(DicomJsonBuilder.New()
                .Str("00080005", "CS", "ISO_IR 192")
                .Str("00080060", "CS", s.Modality)
                .Str("0008103E", "LO", DicomJsonBuilder.Clean(s.Description, 64))
                .Str("00081190", "UR", $"{baseUrl}/studies/{stUid}/series/{s.SeriesUID}")
                .Str("0020000D", "UI", stUid)
                .Str("0020000E", "UI", s.SeriesUID)
                .Int("00200011", "IS", info.SeriesNumber)
                .Int("00201209", "IS", s.Instances));
        }
        return result;
    }

    // ── QIDO-RS: instances ──────────────────────────────────────────────────

    /// <summary>Instances of a study / series. Null when the study (or series) is unknown.</summary>
    public async Task<JsonArray?> SearchInstancesAsync(string? studyUid, string? seriesUid, QidoQuery q, string baseUrl, CancellationToken ct)
    {
        var all = await ResolveAsync(studyUid, seriesUid, null, q.StudyInstanceUids, ct);
        if (all == null) return null;
        IEnumerable<DicomWebInstance> list = all;
        if (q.SeriesInstanceUids.Count > 0) list = list.Where(i => q.SeriesInstanceUids.Contains(i.SeriesUid));
        if (q.SopInstanceUids.Count > 0) list = list.Where(i => q.SopInstanceUids.Contains(i.SopUid));
        if (!string.IsNullOrWhiteSpace(q.Modality)) list = list.Where(i => string.Equals(i.Modality, q.Modality.Trim(), StringComparison.OrdinalIgnoreCase));

        var result = new JsonArray();
        foreach (var i in list.Skip(q.Offset).Take(q.Limit))
        {
            var info = SliceMeta.Read(i.MetadataJson);
            result.Add(DicomJsonBuilder.New()
                .Str("00080016", "UI", info.SopClassUid)
                .Str("00080018", "UI", i.SopUid)
                .Str("00080060", "CS", i.Modality)
                .Str("00081190", "UR", InstanceUrl(baseUrl, i))
                .Str("0020000D", "UI", i.StudyUid)
                .Str("0020000E", "UI", i.SeriesUid)
                .Int("00200011", "IS", info.SeriesNumber)
                .Int("00200013", "IS", i.InstanceNumber)
                .Int("00280008", "IS", info.NumberOfFrames)
                .Int("00280010", "US", info.Rows)
                .Int("00280011", "US", info.Columns));
        }
        return result;
    }

    // ── Resolution ──────────────────────────────────────────────────────────

    /// <summary>
    /// The instances under a study / series / instance path, de-duplicated by SOP
    /// Instance UID and ordered by series then instance number. Null when the study
    /// is not visible; an empty list when the series/instance doesn't exist.
    /// With no studyUid, <paramref name="studyUids"/> (from a QIDO query) must narrow it.
    /// </summary>
    public async Task<List<DicomWebInstance>?> ResolveAsync(
        string? studyUid, string? seriesUid, string? sopUid, IReadOnlyCollection<string>? studyUids, CancellationToken ct)
    {
        var studies = VisibleStudies();
        if (studyUid != null) studies = studies.Where(s => s.StudyInstanceUID == studyUid);
        else if (studyUids is { Count: > 0 }) studies = studies.Where(s => studyUids.Contains(s.StudyInstanceUID!));
        else return null; // all-instances search across the archive is not supported

        var studyRows = await studies.Select(s => new { s.Id, Uid = s.StudyInstanceUID!, s.HospitalId }).Take(100).ToListAsync(ct);
        if (studyRows.Count == 0) return null;
        var ids = studyRows.Select(s => s.Id).ToList();
        var byId = studyRows.ToDictionary(s => s.Id);

        var q = AcquiredSlices().Where(sl => ids.Contains(sl.Asset.ImagingStudyId!.Value));
        if (seriesUid != null) q = q.Where(sl => sl.SeriesUID == seriesUid);
        if (sopUid != null) q = q.Where(sl => sl.SopInstanceUID == sopUid);

        var rows = await q.Select(sl => new
            {
                sl.SliceId,
                StudyId = sl.Asset.ImagingStudyId!.Value,
                sl.SeriesUID,
                sl.SopInstanceUID,
                sl.InstanceNumber,
                sl.BlobUrl,
                sl.MetadataJson,
                sl.SeriesDescription,
                sl.Modality,
                sl.ExtractedAt,
            })
            .ToListAsync(ct);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.BlobUrl))
            .GroupBy(r => r.SopInstanceUID)
            .Select(g => g.OrderByDescending(r => r.ExtractedAt).First())
            .OrderBy(r => r.StudyId).ThenBy(r => r.SeriesUID).ThenBy(r => r.InstanceNumber ?? int.MaxValue).ThenBy(r => r.SopInstanceUID)
            .Select(r => new DicomWebInstance(
                r.SliceId, r.StudyId, byId[r.StudyId].HospitalId, byId[r.StudyId].Uid,
                r.SeriesUID, r.SopInstanceUID, r.InstanceNumber, r.BlobUrl, r.MetadataJson,
                r.SeriesDescription, r.Modality, r.ExtractedAt))
            .ToList();
    }

    public static string InstanceUrl(string baseUrl, DicomWebInstance i) =>
        $"{baseUrl}/studies/{i.StudyUid}/series/{i.SeriesUid}/instances/{i.SopUid}";

    // ── WADO-RS: metadata ───────────────────────────────────────────────────

    /// <summary>
    /// Full DICOM JSON headers (no pixel data) for the given instances. Built once
    /// per series from the stored files and kept: in memory for 15 minutes and as a
    /// blob next to the study, keyed by the exact set of slices, so a re-extraction
    /// or a new upload makes a fresh copy.
    /// </summary>
    public async Task<JsonArray> MetadataAsync(List<DicomWebInstance> instances, string baseUrl, CancellationToken ct)
    {
        var result = new JsonArray();
        foreach (var series in instances.GroupBy(i => (i.StudyId, i.SeriesUid)))
        {
            var list = series.ToList();
            var bySop = await SeriesMetadataAsync(list, ct);
            foreach (var i in list)
            {
                if (!bySop.TryGetValue(i.SopUid, out var json)) continue;
                if (JsonNode.Parse(json) is not JsonObject node) continue;
                node["00081190"] = new JsonObject { ["vr"] = "UR", ["Value"] = new JsonArray(InstanceUrl(baseUrl, i)) };
                result.Add(node);
            }
        }
        return result;
    }

    private async Task<Dictionary<string, string>> SeriesMetadataAsync(List<DicomWebInstance> instances, CancellationToken ct)
    {
        // The cached set covers the WHOLE series; when the caller asked for one
        // instance, build/load the whole series so the cache is reusable.
        var first = instances[0];
        var all = instances;
        if (instances.Count == 1)
            all = await ResolveAsync(first.StudyUid, first.SeriesUid, null, null, ct) ?? instances;

        var fingerprint = Fingerprint(all.Select(i => i.SliceId));
        var cacheKey = $"dicomweb:meta:{first.HospitalId:N}:{first.SeriesUid}:{fingerprint}";
        if (_cache.TryGetValue(cacheKey, out Dictionary<string, string>? cached) && cached != null)
            return cached;

        var blobPath = $"{first.HospitalId:N}/dicomweb-meta/{first.StudyId:N}/{Sha1(first.SeriesUid)}-{fingerprint}.json";
        var map = await TryLoadMetadataBlobAsync(blobPath, ct);
        if (map == null)
        {
            map = await BuildSeriesMetadataAsync(all, ct);
            await TrySaveMetadataBlobAsync(blobPath, map, ct);
        }
        _cache.Set(cacheKey, map, MetadataCacheTtl);
        return map;
    }

    private async Task<Dictionary<string, string>?> TryLoadMetadataBlobAsync(string blobPath, CancellationToken ct)
    {
        try
        {
            var (exists, size) = await _blob.TryGetBlobInfoAsync(blobPath, Container);
            if (!exists || size == 0) return null;
            await using var s = await _blob.DownloadFileAsync(_blob.GetBlobReadUrl(blobPath, Container));
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(s, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DICOMWEB] metadata cache blob {Path} unreadable — rebuilding", blobPath);
            return null;
        }
    }

    private async Task TrySaveMetadataBlobAsync(string blobPath, Dictionary<string, string> map, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(map));
            await _blob.UploadFileAtPathAsync(ms, blobPath, "application/json", Container);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOMWEB] could not store metadata cache blob {Path}", blobPath);
        }
    }

    private async Task<Dictionary<string, string>> BuildSeriesMetadataAsync(List<DicomWebInstance> instances, CancellationToken ct)
    {
        var map = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        using var gate = new SemaphoreSlim(8);
        var tasks = instances.Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var bytes = await DownloadAsync(i.BlobUrl, ct);
                map[i.SopUid] = HeaderJson(bytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[DICOMWEB] header read failed for {Sop} — using the slice index instead", i.SopUid);
                map[i.SopUid] = FallbackJson(i);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return new Dictionary<string, string>(map);
    }

    /// <summary>DICOM JSON of a stored file's header: pixel data and other bulk binary left out.</summary>
    public static string HeaderJson(byte[] dicomBytes)
    {
        using var ms = new MemoryStream(dicomBytes, writable: false);
        var file = DicomFile.Open(ms, FileReadOption.ReadAll);
        var ds = file.Dataset;

        var bulk = ds.Where(item =>
                item is DicomFragmentSequence
                || item.Tag == DicomTag.PixelData
                || item.Tag == DicomTag.FloatPixelData
                || item.Tag == DicomTag.DoubleFloatPixelData
                || (item is DicomElement el && IsBinaryVr(el.ValueRepresentation) && el.Buffer.Size > 1024))
            .Select(item => item.Tag)
            .ToArray();
        if (bulk.Length > 0) ds.Remove(bulk);

        string json;
        try
        {
            json = DicomJson.ConvertDicomToJson(ds, false, false, NumberSerializationMode.PreferablyAsNumber);
        }
        catch
        {
            // Malformed values somewhere — fall back to strings for every number.
            json = DicomJson.ConvertDicomToJson(ds, false, false, NumberSerializationMode.AsString);
        }

        var node = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var ts = file.FileMetaInfo?.TransferSyntax?.UID?.UID ?? ds.InternalTransferSyntax.UID.UID;
        node["00020010"] = new JsonObject { ["vr"] = "UI", ["Value"] = new JsonArray(ts) };
        return node.ToJsonString();
    }

    private static bool IsBinaryVr(DicomVR vr) =>
        vr == DicomVR.OB || vr == DicomVR.OW || vr == DicomVR.OF || vr == DicomVR.OD
        || vr == DicomVR.OL || vr == DicomVR.OV || vr == DicomVR.UN;

    private static string FallbackJson(DicomWebInstance i)
    {
        var info = SliceMeta.Read(i.MetadataJson);
        var o = DicomJsonBuilder.New()
            .Str("00080016", "UI", info.SopClassUid)
            .Str("00080018", "UI", i.SopUid)
            .Str("00080060", "CS", i.Modality)
            .Str("0008103E", "LO", DicomJsonBuilder.Clean(i.SeriesDescription, 64))
            .Str("0020000D", "UI", i.StudyUid)
            .Str("0020000E", "UI", i.SeriesUid)
            .Int("00200013", "IS", i.InstanceNumber)
            .Int("00280010", "US", info.Rows)
            .Int("00280011", "US", info.Columns);
        return o.ToJsonString();
    }

    // ── WADO-RS: instances and frames ───────────────────────────────────────

    /// <summary>
    /// The instance as a DICOM Part 10 file in <paramref name="wantedSyntax"/>.
    /// Stored slices are HTJ2K lossless; when the client can't take that, the file
    /// is decoded on the fly. If re-encoding fails the stored file is sent as is,
    /// labelled with its real transfer syntax.
    /// </summary>
    public async Task<DicomWebPart> ReadInstanceAsync(DicomWebInstance i, string? wantedSyntax, string baseUrl, CancellationToken ct)
    {
        var bytes = await DownloadAsync(i.BlobUrl, ct);
        var location = InstanceUrl(baseUrl, i);

        using var ms = new MemoryStream(bytes, writable: false);
        var file = DicomFile.Open(ms, FileReadOption.ReadAll);
        var stored = file.FileMetaInfo.TransferSyntax;
        if (wantedSyntax == null || wantedSyntax == DicomWebAccept.AnySyntax || wantedSyntax == stored.UID.UID)
            return new DicomWebPart(bytes, PartType(stored.UID.UID), location);

        try
        {
            var target = DicomTransferSyntax.Parse(wantedSyntax);
            var transcoded = new DicomTranscoder(stored, target).Transcode(file);
            using var outMs = new MemoryStream();
            transcoded.Save(outMs);
            return new DicomWebPart(outMs.ToArray(), PartType(target.UID.UID), location);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DICOMWEB] transcode {Sop} {From} → {To} failed — sending stored syntax",
                i.SopUid, stored.UID.UID, wantedSyntax);
            return new DicomWebPart(bytes, PartType(stored.UID.UID), location);
        }

        static string PartType(string ts) => $"application/dicom; transfer-syntax={ts}";
    }

    /// <summary>
    /// The requested frames (1-based) of one instance, either as stored
    /// (compressed) or decoded to uncompressed little-endian pixels, depending on
    /// the Accept header. Null when a frame number is out of range or no Accept
    /// choice can be met (the caller answers 404 / 406).
    /// </summary>
    public async Task<(List<DicomWebPart>? Parts, bool NotAcceptable)> ReadFramesAsync(
        DicomWebInstance i, IReadOnlyList<int> frames, string? acceptHeader, CancellationToken ct)
    {
        var bytes = await DownloadAsync(i.BlobUrl, ct);
        using var ms = new MemoryStream(bytes, writable: false);
        var file = DicomFile.Open(ms, FileReadOption.ReadAll);
        var ds = file.Dataset;
        var stored = file.FileMetaInfo.TransferSyntax;
        var storedUid = stored.UID.UID;

        var choice = DicomWebAccept.FrameSyntax(acceptHeader, storedUid, DicomWebAccept.FrameMediaType(storedUid));
        if (choice == null) return (null, true);

        if (!ds.Contains(DicomTag.PixelData)) return (null, false); // SR, presentation states… have no frames
        var pixel = DicomPixelData.Create(ds);
        var count = pixel.NumberOfFrames;
        if (frames.Count == 0 || frames.Any(f => f < 1 || f > count)) return (null, false);

        var keepStored = choice.Value.Syntax == storedUid || (DicomWebAccept.IsUncompressed(storedUid) && !stored.IsEncapsulated);
        var parts = new List<DicomWebPart>(frames.Count);
        DicomTranscoder? decoder = keepStored ? null : new DicomTranscoder(stored, DicomTransferSyntax.ExplicitVRLittleEndian);
        foreach (var f in frames)
        {
            byte[] data;
            string contentType;
            if (keepStored)
            {
                data = pixel.GetFrame(f - 1).Data;
                contentType = stored.IsEncapsulated
                    ? $"{choice.Value.ContentType}; transfer-syntax={storedUid}"
                    : $"application/octet-stream; transfer-syntax={storedUid}";
            }
            else
            {
                data = decoder!.DecodeFrame(ds, f - 1).Data;
                contentType = $"application/octet-stream; transfer-syntax={DicomWebAccept.ExplicitVrLittleEndian}";
            }
            parts.Add(new DicomWebPart(data, contentType));
        }
        return (parts, false);
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        await using var s = await _blob.DownloadFileAsync(url);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string Fingerprint(IEnumerable<Guid> sliceIds)
    {
        var joined = string.Join(",", sliceIds.OrderBy(g => g).Select(g => g.ToString("N")));
        return Sha1(joined).Substring(0, 12);
    }

    private static string Sha1(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>The few values QIDO needs from a slice's MetadataJson (written by extraction).</summary>
public readonly record struct SliceMeta(string? SopClassUid, int? SeriesNumber, int? NumberOfFrames, int? Rows, int? Columns)
{
    public static SliceMeta Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new SliceMeta(
                Str(r, "sopClassUid"),
                Int(r, "seriesNumber"),
                Int(r, "numberOfFrames"),
                Int(r, "rows"),
                Int(r, "columns"));
        }
        catch
        {
            return default;
        }

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    }
}
