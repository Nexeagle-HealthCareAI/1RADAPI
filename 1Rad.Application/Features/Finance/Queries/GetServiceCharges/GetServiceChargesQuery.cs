using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Finance.Queries.GetServiceCharges;

public record GetServiceChargesQuery : IRequest<List<ServiceChargeDto>>;

public class ServiceChargeDto
{
    public Guid Id { get; set; }
    public string Modality { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal ReferralCutValue { get; set; }
}


public class GetServiceChargesQueryHandler : IRequestHandler<GetServiceChargesQuery, List<ServiceChargeDto>>
{
    private readonly IApplicationDbContext _context;

    public GetServiceChargesQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<ServiceChargeDto>> Handle(GetServiceChargesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<ServiceChargeDto>();
            }

            return await _context.ServiceCharges
                .AsNoTracking()
                .Where(s => s.HospitalId == _context.UserContext.HospitalId)
                .Select(s => new ServiceChargeDto
                {
                    Id = s.Id,
                    Modality = s.Modality,
                    ServiceName = s.ServiceName,
                    Amount = s.Amount,
                    ReferralCutValue = s.ReferralCutValue
                })

                .OrderBy(s => s.Modality)
                .ThenBy(s => s.ServiceName)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve service charges: {ex.Message}", ex);
        }
    }
}
