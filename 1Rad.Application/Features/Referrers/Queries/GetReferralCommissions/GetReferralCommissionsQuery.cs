using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;

public record GetReferralCommissionsQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null,
    DateTime? UpdatedAfter = null,
    bool IncludeDeleted = false
) : IRequest<List<ReferralCommissionDto>>;

public record ReferralCommissionDto(
    Guid Id,
    Guid ReferrerId,
    string? ReferrerName,
    string? Modality,
    decimal Amount,
    decimal AccumulatedTotal,
    DateTime TransactionDate,
    string Status,
    string? ReferenceNumber,
    string? Remarks,
    string? PatientName,
    DateTime? UpdatedAt = null,
    DateTime? DeletedAt = null
);


public class GetReferralCommissionsQueryHandler : IRequestHandler<GetReferralCommissionsQuery, List<ReferralCommissionDto>>
{
    private readonly IApplicationDbContext _context;

    public GetReferralCommissionsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ReferralCommissionDto>> Handle(GetReferralCommissionsQuery request, CancellationToken cancellationToken)
    {
        // 1. Initial query from Commissions — scoped to current hospital (multi-tenant isolation)
        var hospitalId = _context.UserContext.HospitalId;
        var commissionsQuery = _context.ReferralCommissions
            .AsNoTracking()
            .Where(c => c.HospitalId == hospitalId)
            .AsQueryable();

        // 2. Apply Filters
        if (!request.IncludeDeleted)
            commissionsQuery = commissionsQuery.Where(c => c.DeletedAt == null);
        if (request.UpdatedAfter.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.UpdatedAt > request.UpdatedAfter.Value);
        if (request.ReferrerId.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.ReferrerId == request.ReferrerId.Value);

        if (request.StartDate.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.TransactionDate >= request.StartDate.Value);

        if (request.EndDate.HasValue)
            commissionsQuery = commissionsQuery.Where(c => c.TransactionDate <= request.EndDate.Value);

        // 3. Project with Tactical Joins to resolve missing columns (PatientName/ReferrerName) in DB
        return await commissionsQuery
            .OrderByDescending(c => c.TransactionDate)
            .Select(c => new {
                Commission = c,
                ReferrerName = c.Referrer.Name,
                // Join with Appointments/Patients to get the true identity
                PatientName = _context.Appointments
                    .Where(a => a.AppointmentId == c.AppointmentId)
                    .Select(a => a.Patient.FullName)
                    .FirstOrDefault()
            })
            .Select(x => new ReferralCommissionDto(
                x.Commission.Id,
                x.Commission.ReferrerId,
                x.ReferrerName ?? x.Commission.ReferrerName ?? "Unknown Referrer",
                x.Commission.Modality ?? "Unknown",
                x.Commission.CommissionAmount,
                x.Commission.AccumulatedTotal,
                x.Commission.TransactionDate,
                x.Commission.Status ?? "UNPAID",
                x.Commission.ReferenceNumber,
                x.Commission.Remarks,
                x.PatientName ?? "Unknown Patient",
                x.Commission.UpdatedAt,
                x.Commission.DeletedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
