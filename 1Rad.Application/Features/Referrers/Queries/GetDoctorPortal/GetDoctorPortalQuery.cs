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
    string Patient,    // "R. K. · PTID0001"
    string Date,       // yyyy-MM-dd
    string Modality,
    string Status,
    decimal Eligible,
    decimal Paid,
    decimal Unpaid
);

public record DoctorPortalDto(
    string CentreName,
    string DoctorName,
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
        var referrer = await _context.Referrers.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ReferrerId == request.ReferrerId && r.DeletedAt == null, ct);
        if (referrer == null) return null;

        var hospitalId = referrer.HospitalId;
        var centreName = await _context.Hospitals
            .Where(h => h.HospitalId == hospitalId)
            .Select(h => h.HospitalName)
            .FirstOrDefaultAsync(ct);

        var commissions = await _context.ReferralCommissions.AsNoTracking()
            .Where(c => c.ReferrerId == request.ReferrerId && c.HospitalId == hospitalId
                        && c.DeletedAt == null && c.Status != "Cancelled")
            .OrderByDescending(c => c.ServiceDate)
            .Select(c => new { c.PatientName, c.Modality, c.ServiceDate, c.TransactionDate, c.AppointmentId, c.CommissionAmount, c.Status })
            .ToListAsync(ct);

        // Study status + PTID per referred appointment.
        var apptIds = commissions.Where(c => c.AppointmentId != null).Select(c => c.AppointmentId!.Value).Distinct().ToList();
        Dictionary<Guid, (string Status, string Ptid)> infoById = apptIds.Count == 0
            ? new Dictionary<Guid, (string Status, string Ptid)>()
            : (await _context.Appointments.AsNoTracking()
                .Where(a => apptIds.Contains(a.AppointmentId))
                .Select(a => new { a.AppointmentId, a.Status, Ptid = a.Patient.PatientIdentifier })
                .ToListAsync(ct))
              .GroupBy(a => a.AppointmentId)
              .ToDictionary(g => g.Key, g => (g.First().Status ?? string.Empty, g.First().Ptid ?? string.Empty));

        var patients = commissions.Select(c =>
        {
            var paid = string.Equals(c.Status, "PAID", StringComparison.OrdinalIgnoreCase);
            var status = "Pending";
            var ptid = string.Empty;
            if (c.AppointmentId != null && infoById.TryGetValue(c.AppointmentId.Value, out var info))
            {
                if (!string.IsNullOrWhiteSpace(info.Status)) status = info.Status;
                ptid = info.Ptid;
            }
            else if (paid) status = "Completed";

            var date = (c.ServiceDate == default ? c.TransactionDate : c.ServiceDate);
            var label = Mask(c.PatientName);
            if (!string.IsNullOrWhiteSpace(ptid)) label = $"{label} · {ptid}";

            return new DoctorPortalPatientDto(
                label,
                date.ToString("yyyy-MM-dd"),
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
            referrer.Name ?? "Doctor",
            referredCount,
            totalEligible,
            paidTotal,
            totalEligible - paidTotal,
            patients
        );
    }

    // Initials only — a shareable link must never expose a full patient name.
    private static string Mask(string? name)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length == 0) return "—";
        var parts = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts.Take(3).Select(p => char.ToUpperInvariant(p[0]) + "."));
    }
}
