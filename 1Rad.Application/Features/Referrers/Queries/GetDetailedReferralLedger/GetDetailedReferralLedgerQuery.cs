using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetDetailedReferralLedger;

public record DetailedReferralLedgerDto(
    Guid CommissionId,
    DateTime PayoutDate,
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
        
        // 1. Initial query from Commissions scoped to current hospital tenant
        var commissionsQuery = _context.ReferralCommissions
            .AsNoTracking()
            .Where(c => c.HospitalId == hospitalId)
            .AsQueryable();

        // 2. Apply Filters
        if (request.ReferrerId.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.ReferrerId == request.ReferrerId.Value);

        if (request.StartDate.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.TransactionDate >= request.StartDate.Value);

        if (request.EndDate.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.TransactionDate <= request.EndDate.Value);

        // 3. Project clean intermediate structures (EF Core safe translation)
        var rawData = await commissionsQuery
            .Select(c => new {
                CommissionId = c.Id,
                PayoutDate = c.TransactionDate,
                PartnerName = c.Referrer.Name ?? c.ReferrerName,
                PayoutAmount = c.CommissionAmount,
                CommissionStatus = c.Status ?? "UNPAID",
                ReferenceNumber = c.ReferenceNumber,
                
                // Tactical lookup for associated Invoice details
                InvoiceDetails = _context.Invoices
                    .Where(i => i.InvoiceId == c.ReferenceNumber || (c.AppointmentId != null && i.AppointmentId == c.AppointmentId))
                    .Select(i => new {
                        i.InvoiceId,
                        i.PatientName,
                        i.PaidAmount,
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
            x.PartnerName ?? "Unknown Referrer",
            x.InvoiceDetails?.PatientName ?? x.FallbackPatientName ?? "Unknown Patient",
            x.InvoiceDetails != null && x.InvoiceDetails.ItemDescriptions.Any()
                ? string.Join(", ", x.InvoiceDetails.ItemDescriptions)
                : (x.FallbackService ?? "Unknown Service"),
            x.ReferenceNumber ?? x.InvoiceDetails?.InvoiceId ?? "N/A",
            x.PayoutAmount,
            x.InvoiceDetails?.PaidAmount ?? 0,
            x.CommissionStatus,
            x.InvoiceDetails?.Status ?? "PENDING"
        )).ToList();

        return result;
    }
}
