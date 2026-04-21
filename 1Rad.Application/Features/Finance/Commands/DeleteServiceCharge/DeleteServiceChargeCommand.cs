using MediatR;
using _1Rad.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.DeleteServiceCharge;

public record DeleteServiceChargeCommand(Guid Id) : IRequest;

public class DeleteServiceChargeCommandHandler : IRequestHandler<DeleteServiceChargeCommand>
{
    private readonly IApplicationDbContext _context;

    public DeleteServiceChargeCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task Handle(DeleteServiceChargeCommand request, CancellationToken cancellationToken)
    {
        var entity = await _context.ServiceCharges
            .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

        if (entity != null)
        {
            _context.ServiceCharges.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
