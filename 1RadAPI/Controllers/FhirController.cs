using System.Text.Json.Nodes;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Interop;
using _1RadAPI.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace _1RadAPI.Controllers;

/// <summary>
/// Read-only FHIR R4 facade — gap analysis P3-6, phase 4. For a hospital HIS,
/// NexEagle's 1HMS or an ABDM health-information exchange.
///
///   GET api/v1/fhir/metadata                         CapabilityStatement (no auth)
///   GET api/v1/fhir/Patient[/{id}]                   identifier, name, phone, _lastUpdated
///   GET api/v1/fhir/ServiceRequest[/{id}]            patient, identifier, occurrence, status
///   GET api/v1/fhir/ImagingStudy[/{id}]              patient, identifier, started
///   GET api/v1/fhir/DiagnosticReport[/{id}]          patient, based-on, issued, status (signed reports only)
/// Paging: _count (≤200) and _offset; the Bundle carries a "next" link.
///
/// Auth: a staff access token, or an integration key with scope fhir.read.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/fhir")]
public class FhirController : ControllerBase
{
    private readonly IApplicationDbContext _db;
    private readonly IAssetUrlSigner _signer;
    private readonly IConfiguration _config;

    public FhirController(IApplicationDbContext db, IAssetUrlSigner signer, IConfiguration config)
    {
        _db = db;
        _signer = signer;
        _config = config;
    }

    private string ApiBase => InteropUrls.ApiBase(Request, _config);
    private string FhirBase => ApiBase + "/api/v1/fhir";
    private Guid HospitalId => Guid.TryParse(User.FindFirst("cid")?.Value, out var h) ? h : Guid.Empty;

    private ContentResult Fhir(JsonObject o, int status = 200) =>
        new() { Content = o.ToJsonString(), ContentType = FhirMapper.ContentType, StatusCode = status };

    private ContentResult NotFoundOutcome(string type, Guid id) =>
        Fhir(FhirMapper.OperationOutcome("not-found", $"{type}/{id} was not found."), 404);

    private ContentResult Invalid(string message) =>
        Fhir(FhirMapper.OperationOutcome("invalid", message), 400);

    private string Q(string name) => Request.Query[name].ToString();

    private ContentResult Bundle<T>(List<T> page, int total, int offset, int count, Func<T, JsonObject> map)
    {
        var self = $"{FhirBase}{Request.Path.Value!.Substring(Request.Path.Value!.IndexOf("/api/v1/fhir", StringComparison.OrdinalIgnoreCase) + "/api/v1/fhir".Length)}{Request.QueryString}";
        string? next = null;
        if (offset + page.Count < total)
        {
            var qs = Request.Query
                .Where(kv => kv.Key != "_offset")
                .SelectMany(kv => kv.Value.Select(v => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(v ?? string.Empty)}"))
                .Append($"_offset={offset + count}");
            next = $"{FhirBase}{Request.Path.Value!.Substring(Request.Path.Value!.IndexOf("/api/v1/fhir", StringComparison.OrdinalIgnoreCase) + "/api/v1/fhir".Length)}?{string.Join("&", qs)}";
        }
        return Fhir(FhirMapper.SearchBundle(self, FhirBase, page.Select(map), total, next));
    }

    // ── CapabilityStatement ─────────────────────────────────────────────────

    [HttpGet("metadata")]
    [AllowAnonymous]
    public IActionResult Metadata() => Fhir(FhirMapper.CapabilityStatement(FhirBase, DateTime.UtcNow));

    // ── Patient ─────────────────────────────────────────────────────────────

    // Filter BEFORE projecting: EF can't translate a Where on a constructed record.
    private static IQueryable<FhirPatientRow> PatientRows(IQueryable<Patient> q) =>
        q.Select(p => new FhirPatientRow(p.PatientId, p.HospitalId, p.FullName, p.PatientIdentifier, p.Mobile, p.Gender,
            p.Age, p.Address, p.Village, p.Block, p.District, p.UpdatedAt, p.DeletedAt));

    [HttpGet("Patient/{id:guid}")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> ReadPatient(Guid id, CancellationToken ct)
    {
        var row = await PatientRows(_db.Patients.AsNoTracking().Where(p => p.DeletedAt == null && p.PatientId == id)).FirstOrDefaultAsync(ct);
        return row == null ? NotFoundOutcome("Patient", id) : Fhir(FhirMapper.Patient(row));
    }

    [HttpGet("Patient")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> SearchPatients(CancellationToken ct)
    {
        var q = _db.Patients.AsNoTracking().Where(p => p.DeletedAt == null);

        var identifier = Q("identifier");
        if (identifier.Length > 0)
        {
            var (_, value) = FhirSearch.Token(identifier);
            q = q.Where(p => p.PatientIdentifier == value);
        }
        var name = Q("name");
        if (name.Length > 0) q = q.Where(p => p.FullName != null && p.FullName.Contains(name));
        var phone = Q("phone");
        if (phone.Length > 0)
        {
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length >= 10) digits = digits.Substring(digits.Length - 10);
            q = q.Where(p => p.Mobile != null && p.Mobile.EndsWith(digits));
        }
        var updated = Request.Query["_lastUpdated"].Select(v => v ?? string.Empty).ToList();
        if (updated.Count > 0)
        {
            if (!FhirSearch.TryParseDates(updated, out var from, out var to)) return Invalid("Bad _lastUpdated value.");
            if (from != null) { var f = IstDateRange.ToUtcStart(from.Value) + from.Value.TimeOfDay; q = q.Where(p => p.UpdatedAt >= f); }
            if (to != null) { var t = IstDateRange.ToUtcStart(to.Value) + to.Value.TimeOfDay; q = q.Where(p => p.UpdatedAt < t); }
        }

        var count = FhirSearch.Count(Q("_count"));
        var offset = FhirSearch.Offset(Q("_offset"));
        var total = await q.CountAsync(ct);
        var page = await PatientRows(q.OrderBy(p => p.FullName).ThenBy(p => p.PatientId).Skip(offset).Take(count)).ToListAsync(ct);
        return Bundle(page, total, offset, count, FhirMapper.Patient);
    }

    // ── ServiceRequest ──────────────────────────────────────────────────────

    private IQueryable<AppointmentService> Services() =>
        _db.AppointmentServices.AsNoTracking().Where(s => s.DeletedAt == null && s.Appointment.DeletedAt == null);

    private static IQueryable<FhirServiceRequestRow> ServiceRows(IQueryable<AppointmentService> q) =>
        q.Select(s => new FhirServiceRequestRow(
            s.Id, s.HospitalId, s.AppointmentId, s.Appointment.DisplayId, s.Status, s.Appointment.Status,
            s.Appointment.Priority, s.ServiceName, s.Modality, s.Appointment.DateTime, s.Appointment.PatientId,
            s.Appointment.PatientName, s.Appointment.ReferredBy, s.UpdatedAt, s.CancelledAt));

    [HttpGet("ServiceRequest/{id:guid}")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> ReadServiceRequest(Guid id, CancellationToken ct)
    {
        var row = await ServiceRows(Services().Where(s => s.Id == id)).FirstOrDefaultAsync(ct);
        return row == null ? NotFoundOutcome("ServiceRequest", id) : Fhir(FhirMapper.ServiceRequest(row));
    }

    [HttpGet("ServiceRequest")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> SearchServiceRequests(CancellationToken ct)
    {
        var q = Services();
        var patient = Q("patient");
        if (patient.Length > 0)
        {
            var pid = FhirSearch.ReferenceId(patient, "Patient");
            if (pid == null) return Invalid("patient must be Patient/{id}.");
            q = q.Where(s => s.Appointment.PatientId == pid.Value);
        }
        var identifier = Q("identifier");
        if (identifier.Length > 0)
        {
            var (_, value) = FhirSearch.Token(identifier);
            q = Guid.TryParse(value, out var g)
                ? q.Where(s => s.AppointmentId == g)
                : q.Where(s => s.Appointment.DisplayId == value);
        }
        var occurrence = Request.Query["occurrence"].Select(v => v ?? string.Empty).ToList();
        if (occurrence.Count > 0)
        {
            if (!FhirSearch.TryParseDates(occurrence, out var from, out var to)) return Invalid("Bad occurrence value.");
            if (from != null) { var f = IstDateRange.ToUtcStart(from.Value) + from.Value.TimeOfDay; q = q.Where(s => s.Appointment.DateTime >= f); }
            if (to != null) { var t = IstDateRange.ToUtcStart(to.Value) + to.Value.TimeOfDay; q = q.Where(s => s.Appointment.DateTime < t); }
        }
        var status = Q("status").Trim().ToLowerInvariant();
        if (status == "revoked")
            q = q.Where(s => s.CancelledAt != null || s.Status == "CANCELLED" || s.Appointment.Status == "CANCELLED");
        else if (status == "completed")
            q = q.Where(s => s.CancelledAt == null && (s.Status == "REPORTED" || s.Status == "DELIVERED"
                || s.Appointment.Status == "REPORTED" || s.Appointment.Status == "DELIVERED" || s.Appointment.Status == "COMPLETED"));
        else if (status == "active")
            q = q.Where(s => s.CancelledAt == null && s.Status != "CANCELLED" && s.Status != "REPORTED" && s.Status != "DELIVERED"
                && s.Appointment.Status != "CANCELLED" && s.Appointment.Status != "REPORTED"
                && s.Appointment.Status != "DELIVERED" && s.Appointment.Status != "COMPLETED");
        else if (status.Length > 0) return Invalid("status must be active, completed or revoked.");

        var count = FhirSearch.Count(Q("_count"));
        var offset = FhirSearch.Offset(Q("_offset"));
        var total = await q.CountAsync(ct);
        var page = await ServiceRows(q.OrderByDescending(s => s.Appointment.DateTime).ThenBy(s => s.Id).Skip(offset).Take(count))
            .ToListAsync(ct);
        return Bundle(page, total, offset, count, FhirMapper.ServiceRequest);
    }

    // ── ImagingStudy ────────────────────────────────────────────────────────

    [HttpGet("ImagingStudy/{id:guid}")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> ReadImagingStudy(Guid id, CancellationToken ct)
    {
        var rows = await ImagingRowsAsync(_db.ImagingStudies.AsNoTracking().Where(s => s.Id == id), ct);
        return rows.Count == 0 ? NotFoundOutcome("ImagingStudy", id) : Fhir(FhirMapper.ImagingStudy(rows[0], ApiBase + "/api/v1/dicomweb"));
    }

    [HttpGet("ImagingStudy")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> SearchImagingStudies(CancellationToken ct)
    {
        var q = _db.ImagingStudies.AsNoTracking().AsQueryable();
        var patient = Q("patient");
        if (patient.Length > 0)
        {
            var pid = FhirSearch.ReferenceId(patient, "Patient");
            if (pid == null) return Invalid("patient must be Patient/{id}.");
            q = q.Where(s => s.PatientId == pid.Value);
        }
        var identifier = Q("identifier");
        if (identifier.Length > 0)
        {
            var (_, value) = FhirSearch.Token(identifier);
            var uid = value.StartsWith("urn:oid:", StringComparison.OrdinalIgnoreCase) ? value.Substring(8) : value;
            q = q.Where(s => s.StudyInstanceUID == uid || s.AccessionNumber == value);
        }
        var started = Request.Query["started"].Select(v => v ?? string.Empty).ToList();
        if (started.Count > 0)
        {
            // StudyDate is stored as the modality's local (India) time — compare as is.
            if (!FhirSearch.TryParseDates(started, out var from, out var to)) return Invalid("Bad started value.");
            if (from != null) { var f = from.Value; q = q.Where(s => s.StudyDate >= f); }
            if (to != null) { var t = to.Value; q = q.Where(s => s.StudyDate < t); }
        }

        var count = FhirSearch.Count(Q("_count"));
        var offset = FhirSearch.Offset(Q("_offset"));
        var total = await q.CountAsync(ct);
        var page = await ImagingRowsAsync(q.OrderByDescending(s => s.StudyDate).ThenByDescending(s => s.CreatedAt).Skip(offset).Take(count), ct);
        var dicomWeb = ApiBase + "/api/v1/dicomweb";
        return Bundle(page, total, offset, count, r => FhirMapper.ImagingStudy(r, dicomWeb));
    }

    private async Task<List<FhirImagingStudyRow>> ImagingRowsAsync(IQueryable<ImagingStudy> q, CancellationToken ct)
    {
        var studies = await q.Select(s => new
            {
                s.Id, s.HospitalId, s.StudyInstanceUID, s.Status, s.PatientId,
                PatientName = s.Patient != null ? s.Patient.FullName : s.PatientName,
                s.DicomPatientId, s.AccessionNumber, s.Modality, s.StudyDate, s.StudyDescription,
                s.AppointmentServiceId, s.CreatedAt,
            })
            .ToListAsync(ct);
        if (studies.Count == 0) return new List<FhirImagingStudyRow>();

        var ids = studies.Select(s => s.Id).ToList();
        var slices = await _db.StudySliceIndexes.AsNoTracking()
            .Where(sl => sl.Asset.ImagingStudyId != null && ids.Contains(sl.Asset.ImagingStudyId.Value)
                         && (sl.SeriesDescription == null || !sl.SeriesDescription.EndsWith("(MPR)")))
            .GroupBy(sl => new { StudyId = sl.Asset.ImagingStudyId!.Value, sl.SeriesUID })
            .Select(g => new
            {
                g.Key.StudyId,
                g.Key.SeriesUID,
                Modality = g.Max(x => x.Modality),
                Description = g.Max(x => x.SeriesDescription),
                Instances = g.Select(x => x.SopInstanceUID).Distinct().Count(),
            })
            .ToListAsync(ct);
        var byStudy = slices.GroupBy(x => x.StudyId).ToDictionary(g => g.Key,
            g => (IReadOnlyList<FhirSeriesRow>)g.Select(x => new FhirSeriesRow(x.SeriesUID, x.Modality, x.Description, x.Instances, null)).ToList());

        return studies.Select(s => new FhirImagingStudyRow(
                s.Id, s.HospitalId, s.StudyInstanceUID, s.Status, s.PatientId, s.PatientName, s.DicomPatientId,
                s.AccessionNumber, s.Modality, s.StudyDate, s.StudyDescription, s.AppointmentServiceId, s.CreatedAt,
                byStudy.TryGetValue(s.Id, out var series) ? series : Array.Empty<FhirSeriesRow>()))
            .ToList();
    }

    // ── DiagnosticReport (signed only) ──────────────────────────────────────

    private IQueryable<DiagnosticReport> SignedReports() =>
        _db.DiagnosticReports.AsNoTracking()
            .Where(r => r.DeletedAt == null
                        && (r.Status == ReportStatuses.Preliminary || r.Status == ReportStatuses.Final || r.Status == ReportStatuses.Addended));

    private async Task<List<FhirReportRow>> ReportRowsAsync(IQueryable<DiagnosticReport> q, CancellationToken ct)
    {
        var rows = await q.Select(r => new
            {
                r.Id, r.HospitalId, r.Status,
                PatientId = r.Appointment != null ? (Guid?)r.Appointment.PatientId
                          : r.ImagingStudy != null ? r.ImagingStudy.PatientId : null,
                PatientName = r.Appointment != null ? r.Appointment.PatientName
                            : r.ImagingStudy != null ? r.ImagingStudy.PatientName : null,
                r.ImagingStudyId, r.AppointmentServiceId, r.Impression, r.Findings, r.Advice,
                r.SignerName, r.SignerCredentials, r.SignedAt, r.FinalizedAt, r.UpdatedAt, r.ReportPdfUrl,
                Addenda = r.Addenda.Count(),
                ServiceName = _db.AppointmentServices.Where(s => s.Id == r.AppointmentServiceId).Select(s => s.ServiceName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var ttl = TimeSpan.FromHours(_config.GetValue("AssetProxy:SignedUrlTtlHours", 8));
        var apiBase = ApiBase;
        return rows.Select(r => new FhirReportRow(
                r.Id, r.HospitalId, r.Status, r.PatientId, r.PatientName, r.ImagingStudyId, r.AppointmentServiceId,
                r.Impression, r.Findings, r.Advice, r.SignerName, r.SignerCredentials, r.SignedAt, r.FinalizedAt,
                r.UpdatedAt, SignedPdfUrl(r.ReportPdfUrl, apiBase, ttl), r.Addenda, r.ServiceName))
            .ToList();
    }

    /// <summary>A time-limited link to the signed PDF, through the API's signed asset proxy.</summary>
    private string? SignedPdfUrl(string? blobUrl, string apiBase, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(blobUrl)) return null;
        var (exp, sig) = _signer.Sign(blobUrl, ttl);
        return $"{apiBase}/api/v1/Study/proxy-asset?url={Uri.EscapeDataString(blobUrl)}&exp={exp}&sig={Uri.EscapeDataString(sig)}";
    }

    [HttpGet("DiagnosticReport/{id:guid}")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> ReadDiagnosticReport(Guid id, CancellationToken ct)
    {
        var rows = await ReportRowsAsync(SignedReports().Where(r => r.Id == id), ct);
        return rows.Count == 0 ? NotFoundOutcome("DiagnosticReport", id) : Fhir(FhirMapper.DiagnosticReport(rows[0]));
    }

    [HttpGet("DiagnosticReport")]
    [IntegrationScope(IntegrationScopes.FhirRead)]
    public async Task<IActionResult> SearchDiagnosticReports(CancellationToken ct)
    {
        var q = SignedReports();
        var patient = Q("patient");
        if (patient.Length > 0)
        {
            var pid = FhirSearch.ReferenceId(patient, "Patient");
            if (pid == null) return Invalid("patient must be Patient/{id}.");
            q = q.Where(r => (r.Appointment != null && r.Appointment.PatientId == pid.Value)
                             || (r.ImagingStudy != null && r.ImagingStudy.PatientId == pid.Value));
        }
        var basedOn = Q("based-on");
        if (basedOn.Length > 0)
        {
            var sid = FhirSearch.ReferenceId(basedOn, "ServiceRequest");
            if (sid == null) return Invalid("based-on must be ServiceRequest/{id}.");
            q = q.Where(r => r.AppointmentServiceId == sid.Value);
        }
        var issued = Request.Query["issued"].Select(v => v ?? string.Empty).ToList();
        if (issued.Count > 0)
        {
            if (!FhirSearch.TryParseDates(issued, out var from, out var to)) return Invalid("Bad issued value.");
            if (from != null) { var f = IstDateRange.ToUtcStart(from.Value) + from.Value.TimeOfDay; q = q.Where(r => (r.SignedAt ?? r.FinalizedAt) >= f); }
            if (to != null) { var t = IstDateRange.ToUtcStart(to.Value) + to.Value.TimeOfDay; q = q.Where(r => (r.SignedAt ?? r.FinalizedAt) < t); }
        }
        var status = Q("status").Trim().ToLowerInvariant();
        if (status == "preliminary") q = q.Where(r => r.Status == ReportStatuses.Preliminary);
        else if (status == "final") q = q.Where(r => r.Status == ReportStatuses.Final);
        else if (status == "appended") q = q.Where(r => r.Status == ReportStatuses.Addended);
        else if (status.Length > 0) return Invalid("status must be preliminary, final or appended.");

        var count = FhirSearch.Count(Q("_count"));
        var offset = FhirSearch.Offset(Q("_offset"));
        var total = await q.CountAsync(ct);
        var page = await ReportRowsAsync(q.OrderByDescending(r => r.SignedAt ?? r.FinalizedAt).ThenBy(r => r.Id).Skip(offset).Take(count), ct);
        return Bundle(page, total, offset, count, FhirMapper.DiagnosticReport);
    }
}
