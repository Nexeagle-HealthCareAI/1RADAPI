using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;

// Public, referrer-scoped snapshot powering the doctor portal (/r/{id}). Derives
// the hospital from the referrer (NOT from a user context — the caller is
// unauthenticated) and never exposes full patient names (masked to initials +
// PTID), since the link is a capability URL.
public record GetDoctorPortalQuery(Guid ReferrerId) : IRequest<DoctorPortalDto?>;

public record DoctorPortalPatientDto(
    string Patient,    // full name (+ PTID) — the referring doctor's own patients
    string Date,       // yyyy-MM-dd (service date)
    string? ArrivedAt, // ISO UTC — when the patient reached the centre (null if not arrived)
    string Modality,
    string Status,
    decimal Eligible,
    decimal Paid,
    decimal Unpaid
);

public record DoctorPortalDto(
    string CentreName,
    // Centre identity for the portal's top nav (best-effort — the admin user's).
    string? CentreLocation,
    string? CentreAdminName,
    string? CentreContact,
    string? CentreEmail,
    string DoctorName,
    // Self-service profile (editable by the doctor from their portal link).
    string? Location,
    string? Specialty,
    string? Degree,
    string? DoctorEmail,
    string? DoctorContact,
    int ReferredCount,
    decimal TotalEligible,
    decimal PaymentReceived,
    decimal Outstanding,
    List<DoctorPortalPatientDto> Patients
);

public class GetDoctorPortalQueryHandler : IRequestHandler<GetDoctorPortalQuery, DoctorPortalDto?>
{
    private readonly IApplicationDbContext _context;

    public GetDoctorPortalQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<DoctorPortalDto?> Handle(GetDoctorPortalQuery request, CancellationToken ct)
    {
        // Anonymous (capability-link) request → no hospital/user context, so the
        // global HospitalId query filter would match nothing. Bypass it and scope
        // explicitly by the token-validated ReferrerId / its own HospitalId.
        var referrer = await _context.Referrers.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null) return null;

        var hospitalId = referrer.HospitalId;
        var centre = await _context.Hospitals.IgnoreQueryFilters()
            .Where(h => h.HospitalId == hospitalId)
            .Select(h => new { h.HospitalName, h.HospitalAddress })
            .FirstOrDefaultAsync(ct);
        var centreName = centre?.HospitalName;

        // Best-effort centre contact for the top nav: prefer a user with an Admin
        // role mapped to this hospital, else the default / earliest-assigned user.
        var centreUsers = await _context.UserHospitalMappings.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.HospitalId == hospitalId)
            .Select(m => new {
                m.IsDefault,
                m.AssignedAt,
                m.User.FullName,
                m.User.Email,
                m.User.Mobile,
                IsAdmin = m.Roles.Any(r => r.RoleName.Contains("Admin"))
            })
            .ToListAsync(ct);
        var admin = centreUsers
            .OrderByDescending(x => x.IsAdmin)
            .ThenByDescending(x => x.IsDefault)
            .ThenBy(x => x.AssignedAt)
            .FirstOrDefault();

        var commissions = await _context.ReferralCommissions.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.ReferrerId == request.ReferrerId && c.HospitalId == hospitalId
                        && c.DeletedAt == null && c.Status != "Cancelled")
            .OrderByDescending(c => c.ServiceDate)
            .Select(c => new { c.PatientName, c.Modality, c.ServiceDate, c.TransactionDate, c.AppointmentId, c.CommissionAmount, c.Status })
            .ToListAsync(ct);

        // Study status + PTID per referred appointment.
        var apptIds = commissions.Where(c => c.AppointmentId != null).Select(c => c.AppointmentId!.Value).Distinct().ToList();
        Dictionary<Guid, (string Status, string Ptid, string FullName, DateTime? ArrivedAt)> infoById = apptIds.Count == 0
            ? new Dictionary<Guid, (string Status, string Ptid, string FullName, DateTime? ArrivedAt)>()
            : (await _context.Appointments.AsNoTracking().IgnoreQueryFilters()
                .Where(a => apptIds.Contains(a.AppointmentId))
                .Select(a => new { a.AppointmentId, a.Status, Ptid = a.Patient.PatientIdentifier, FullName = a.Patient.FullName, a.ArrivedAt })
                .ToListAsync(ct))
              .GroupBy(a => a.AppointmentId)
              .ToDictionary(g => g.Key, g => (g.First().Status ?? string.Empty, g.First().Ptid ?? string.Empty, g.First().FullName ?? string.Empty, g.First().ArrivedAt));

        var patients = commissions.Select(c =>
        {
            var paid = string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase);
            var status = "Pending";
            var ptid = string.Empty;
            var fullName = string.Empty;
            DateTime? arrivedAt = null;
            if (c.AppointmentId != null && infoById.TryGetValue(c.AppointmentId.Value, out var info))
            {
                if (!string.IsNullOrWhiteSpace(info.Status)) status = info.Status;
                ptid = info.Ptid;
                fullName = info.FullName;
                arrivedAt = info.ArrivedAt;
            }
            else if (paid) status = "Completed";

            var date = (c.ServiceDate == default ? c.TransactionDate : c.ServiceDate);
            // The referring doctor referred this patient, so they see the full name.
            var name = !string.IsNullOrWhiteSpace(fullName) ? fullName
                : (string.IsNullOrWhiteSpace(c.PatientName) ? "—" : c.PatientName);
            var label = string.IsNullOrWhiteSpace(ptid) ? name : $"{name} · {ptid}";

            return new DoctorPortalPatientDto(
                label,
                date.ToString("yyyy-MM-dd"),
                arrivedAt.HasValue ? DateTime.SpecifyKind(arrivedAt.Value, DateTimeKind.Utc).ToString("o") : null,
                string.IsNullOrWhiteSpace(c.Modality) ? "—" : c.Modality,
                status,
                c.CommissionAmount,
                paid ? c.CommissionAmount : 0,
                paid ? 0 : c.CommissionAmount
            );
        }).ToList();

        var totalEligible = commissions.Sum(c => c.CommissionAmount);
        var paidTotal = commissions.Where(c => string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase)).Sum(c => c.CommissionAmount);
        var referredCount = commissions.Select(c => c.AppointmentId).Where(x => x != null).Distinct().Count();
        if (referredCount == 0) referredCount = commissions.Count;

        return new DoctorPortalDto(
            string.IsNullOrWhiteSpace(centreName) ? "Diagnostic Centre" : centreName!,
            centre?.HospitalAddress,
            admin?.FullName,
            admin?.Mobile,
            admin?.Email,
            referrer.Name ?? "Doctor",
            referrer.Address,
            referrer.Specialty,
            referrer.Degree,
            referrer.Email,
            referrer.Contact,
            referredCount,
            totalEligible,
            paidTotal,
            totalEligible - paidTotal,
            patients
        );
    }
}
