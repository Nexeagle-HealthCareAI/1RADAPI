using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetDetailedReferralLedger;

public record DetailedReferralLedgerDto(
    Guid CommissionId,
    DateTime PayoutDate,
    DateTime ServiceDate,
    string PartnerName,
    string PatientName,
    string StudyAndServices,
    string ReferenceNumber,
    decimal PayoutAmount,
    decimal PaymentReceived,
    string CommissionStatus,
    string PatientPaymentStatus
);

public record GetDetailedReferralLedgerQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null
) : IRequest<List<DetailedReferralLedgerDto>>;

public class GetDetailedReferralLedgerQueryHandler : IRequestHandler<GetDetailedReferralLedgerQuery, List<DetailedReferralLedgerDto>>
{
    private readonly IApplicationDbContext _context;

    public GetDetailedReferralLedgerQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<DetailedReferralLedgerDto>> Handle(GetDetailedReferralLedgerQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _context.UserContext.HospitalId;
        
        // 1. Initial query from Commissions scoped to current hospital tenant.
        //    Exclude soft-deleted rows (e.g. commissions removed when an
        //    appointment was cancelled) so the ledger matches the live data.
        var commissionsQuery = _context.ReferralCommissions
            .AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null)
            .AsQueryable();

        // 2. Apply Filters
        if (request.ReferrerId.HasValue)
        {
            // Include every partner merged into the requested one.
            var registry = await _context.Referrers.AsNoTracking()
                .Where(r => r.HospitalId == hospitalId)
                .Select(r => new { r.ReferrerId, r.MergedIntoId })
                .ToListAsync(cancellationToken);
            var byId = registry.ToDictionary(r => r.ReferrerId, r => r.MergedIntoId);
            Guid Root(Guid id)
            {
                var seen = new HashSet<Guid>();
                while (byId.TryGetValue(id, out var next) && next.HasValue && seen.Add(id)) id = next.Value;
                return id;
            }
            var root = Root(request.ReferrerId.Value);
            var aliasIds = registry.Where(r => Root(r.ReferrerId) == root).Select(r => r.ReferrerId).ToList();
            if (!aliasIds.Contains(request.ReferrerId.Value)) aliasIds.Add(request.ReferrerId.Value);
            commissionsQuery = commissionsQuery.Where(c => aliasIds.Contains(c.ReferrerId));
        }

        // Bucket by the day of the SERVICE (IST), like the commission list and every
        // other report — not by when the row happened to be recorded — and treat the
        // end date as the whole IST day. (A bare-date EndDate is midnight, so the old
        // "<= EndDate" silently dropped the entire last day.)
        if (request.StartDate.HasValue)
        {
            var fromUtc = IstDateRange.ToUtcStart(request.StartDate.Value);
            commissionsQuery = commissionsQuery.Where(c => c.ServiceDate >= fromUtc);
        }
        if (request.EndDate.HasValue)
        {
            var toUtc = IstDateRange.ToUtcEndInclusive(request.EndDate.Value);
            commissionsQuery = commissionsQuery.Where(c => c.ServiceDate <= toUtc);
        }

        // 3. Project clean intermediate structures (EF Core safe translation)
        var rawData = await commissionsQuery
            .Select(c => new {
                CommissionId = c.Id,
                PayoutDate = c.TransactionDate,
                // The actual appointment/service date — drives the "upcoming vs
                // earned" split in the Referral Hub (a future date = not yet earned).
                ServiceDate = c.ServiceDate,
                PartnerName = c.Referrer.Name ?? c.ReferrerName,
                PayoutAmount = c.CommissionAmount,
                CommissionStatus = c.Status ?? "UNPAID",
                ReferenceNumber = c.ReferenceNumber,
                
                // Tactical lookup for associated Invoice details. Match on
                // AppointmentId first (most reliable link), then fall back to
                // the display InvoiceId stored in the commission's reference.
                InvoiceDetails = _context.Invoices
                    .Where(i => i.DeletedAt == null
                                && ((c.AppointmentId != null && i.AppointmentId == c.AppointmentId)
                                    || (c.ReferenceNumber != null && i.InvoiceId == c.ReferenceNumber)))
                    .OrderByDescending(i => i.PaidAmount)
                    .Select(i => new {
                        i.InvoiceId,
                        i.PatientName,
                        i.PaidAmount,
                        i.TotalAmount,
                        i.Status,
                        ItemDescriptions = i.Items.Select(it => it.Description)
                    })
                    .FirstOrDefault(),
                
                // Fallbacks if no invoice is mapped yet
                FallbackPatientName = _context.Appointments
                    .Where(a => a.AppointmentId == c.AppointmentId)
                    .Select(a => a.Patient.FullName)
                    .FirstOrDefault(),
                
                FallbackService = _context.Appointments
                    .Where(a => a.AppointmentId == c.AppointmentId)
                    .Select(a => a.Service)
                    .FirstOrDefault() ?? c.Modality
            })
            .OrderByDescending(x => x.PayoutDate)
            .ToListAsync(cancellationToken);

        // 4. Map to final DTO and format aggregates in-memory
        var result = rawData.Select(x => new DetailedReferralLedgerDto(
            x.CommissionId,
            x.PayoutDate,
            // Unset ServiceDate (legacy rows) falls back to the transaction date.
            x.ServiceDate == default ? x.PayoutDate : x.ServiceDate,
            x.PartnerName ?? "Unknown Referrer",
            x.InvoiceDetails?.PatientName ?? x.FallbackPatientName ?? "Unknown Patient",
            x.InvoiceDetails != null && x.InvoiceDetails.ItemDescriptions.Any()
                ? string.Join(", ", x.InvoiceDetails.ItemDescriptions)
                : (x.FallbackService ?? "Unknown Service"),
            x.ReferenceNumber ?? x.InvoiceDetails?.InvoiceId ?? "N/A",
            x.PayoutAmount,
            x.InvoiceDetails?.PaidAmount ?? 0,
            x.CommissionStatus,
            // Derive patient payment status from the actual amounts rather than
            // trusting the stored Status string (which can be stale/casing-variant).
            // This is what unblocks paying the referrer once the patient has paid.
            PatientPaymentStatus.Resolve(x.InvoiceDetails?.PaidAmount, x.InvoiceDetails?.TotalAmount, x.InvoiceDetails?.Status)
        )).ToList();

        return result;
    }
}
