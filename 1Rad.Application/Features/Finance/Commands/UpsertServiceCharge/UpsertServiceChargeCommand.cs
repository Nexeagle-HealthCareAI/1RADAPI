using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;

public record UpsertServiceChargeCommand : IRequest<Guid>
{
    public Guid? Id { get; init; }
    public string Modality { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
}

public class UpsertServiceChargeCommandHandler : IRequestHandler<UpsertServiceChargeCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public UpsertServiceChargeCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Guid> Handle(UpsertServiceChargeCommand request, CancellationToken cancellationToken)
    {
        ServiceCharge? entity;

        if (request.Id.HasValue && request.Id != Guid.Empty)
        {
            entity = await _context.ServiceCharges
                .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
            
            if (entity == null) throw new Exception("Service charge not found.");
        }
        else
        {
            entity = new ServiceCharge
            {
                HospitalId = _context.UserContext.HospitalId
            };
            _context.ServiceCharges.Add(entity);
        }

        entity.Modality = request.Modality;
        entity.ServiceName = request.ServiceName;
        entity.Amount = request.Amount;

        await _context.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }
}
