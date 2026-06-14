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
    decimal Unpaid,
    decimal Total,         // total eligible incentive (net incentive + concession given)
    decimal Discount,      // referrer concession attributed to this referral (pro-rata per service)
    bool Arrived,          // has the patient reached the centre yet?
    string PaymentStatus   // patient's invoice payment: PAID | PARTIAL | UNPAID (drives eligibility)
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
            .Select(c => new { c.PatientName, c.Modality, c.ServiceDate, c.TransactionDate, c.AppointmentId, c.AppointmentServiceId, c.CommissionAmount, c.Status })
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

        // Per-visit invoice lines so each referral row can show the patient's bill
        // for that study and the referrer concession on it. One invoice per visit;
        // a line maps 1:1 to an AppointmentService (migration 57), so we match the
        // commission's service line for the per-study price and allocate the
        // invoice's ReferrerDiscount pro-rata across its lines. Flattened via
        // SelectMany (a join) to avoid a nested collection projection.
        var invoiceLines = apptIds.Count == 0
            ? new List<InvoiceLine>()
            : await _context.Invoices.AsNoTracking().IgnoreQueryFilters()
                .Where(inv => inv.AppointmentId != null && apptIds.Contains(inv.AppointmentId.Value)
                              && inv.DeletedAt == null && inv.Status != "CANCELLED")
                .SelectMany(inv => inv.Items.Select(it => new InvoiceLine(
                    inv.AppointmentId, inv.GrossAmount, inv.ReferrerDiscount,
                    it.AppointmentServiceId, it.Amount, it.Quantity, inv.PaidAmount, inv.TotalAmount)))
                .ToListAsync(ct);
        var linesByAppt = invoiceLines
            .Where(l => l.AppointmentId != null)
            .GroupBy(l => l.AppointmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

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

            // The referrer concession given to the patient out of this doctor's cut.
            // Allocated pro-rata by the referred service's share of the bill (the
            // ReferrerDiscount is invoice-level; one service = one line, migration 57).
            decimal discount = 0m;
            // Patient's invoice payment — drives portal eligibility. No invoice yet
            // (e.g. booked-but-not-arrived) reads as UNPAID.
            var paymentStatus = "UNPAID";
            if (c.AppointmentId != null && linesByAppt.TryGetValue(c.AppointmentId.Value, out var lines) && lines.Count > 0)
            {
                var gross = lines.Sum(x => x.Amount * x.Quantity);
                if (gross <= 0) gross = lines[0].GrossAmount;
                var refDiscount = lines[0].ReferrerDiscount;

                var line = c.AppointmentServiceId != null
                    ? lines.FirstOrDefault(x => x.AppointmentServiceId == c.AppointmentServiceId)
                    : null;
                decimal lineSubtotal = line != null ? line.Amount * line.Quantity
                    : (lines.Count == 1 ? lines[0].Amount * lines[0].Quantity : gross);

                var share = gross > 0 ? lineSubtotal / gross : 1m;
                discount = Math.Round(refDiscount * share, 2);

                var net = lines[0].TotalAmount;
                var invPaid = lines[0].PaidAmount;
                if (net <= 0 || invPaid >= net - 0.01m) paymentStatus = "PAID";
                else if (invPaid > 0) paymentStatus = "PARTIAL";
                else paymentStatus = "UNPAID";
            }

            // "Total amount" on the portal = the doctor's TOTAL ELIGIBLE INCENTIVE
            // (gross): what they net (CommissionAmount, which is already after the
            // concession) plus the concession they gave the patient. Net + discount.
            var total = c.CommissionAmount + discount;
            var arrived = arrivedAt.HasValue;

            return new DoctorPortalPatientDto(
                label,
                date.ToString("yyyy-MM-dd"),
                arrivedAt.HasValue ? DateTime.SpecifyKind(arrivedAt.Value, DateTimeKind.Utc).ToString("o") : null,
                string.IsNullOrWhiteSpace(c.Modality) ? "—" : c.Modality,
                status,
                c.CommissionAmount,
                paid ? c.CommissionAmount : 0,
                paid ? 0 : c.CommissionAmount,
                total,
                discount,
                arrived,
                paymentStatus
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

    // Flattened invoice-line projection (one row per line, carrying its parent
    // invoice's gross + referrer discount) used to price each referral row.
    private sealed record InvoiceLine(
        Guid? AppointmentId,
        decimal GrossAmount,
        decimal ReferrerDiscount,
        Guid? AppointmentServiceId,
        decimal Amount,
        int Quantity,
        decimal PaidAmount,
        decimal TotalAmount);
}
