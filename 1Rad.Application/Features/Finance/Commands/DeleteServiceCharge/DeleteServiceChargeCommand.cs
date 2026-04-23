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
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                throw new UnauthorizedAccessException("Hospital context is required to delete service charges.");
            }

            var entity = await _context.ServiceCharges
                .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);

            if (entity == null)
            {
                throw new KeyNotFoundException($"Service charge with ID '{request.Id}' not found.");
            }

            // Verify ownership
            if (entity.HospitalId != _context.UserContext.HospitalId)
            {
                throw new UnauthorizedAccessException("You do not have permission to delete this service charge.");
            }

            _context.ServiceCharges.Remove(entity);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to delete service charge: {ex.Message}", ex);
        }
    }
}
