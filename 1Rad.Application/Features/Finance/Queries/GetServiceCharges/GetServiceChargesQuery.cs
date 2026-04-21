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
        return await _context.ServiceCharges
            .Select(s => new ServiceChargeDto
            {
                Id = s.Id,
                Modality = s.Modality,
                ServiceName = s.ServiceName,
                Amount = s.Amount
            })
            .ToListAsync(cancellationToken);
    }
}
