using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Finance.Commands.UpsertServiceCharge;

public record UpsertServiceChargeCommand : IRequest<Guid>
{
    public Guid? Id { get; init; }
    public string Modality { init; get; } = string.Empty;
    public string ServiceName { init; get; } = string.Empty;
    public decimal Amount { init; get; }
    public decimal ReferralCutValue { init; get; } = 0;
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
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                throw new UnauthorizedAccessException("Hospital context is required to manage service charges.");
            }

            // Validate inputs
            if (string.IsNullOrWhiteSpace(request.Modality))
            {
                throw new ArgumentException("Modality is required.", nameof(request.Modality));
            }

            if (string.IsNullOrWhiteSpace(request.ServiceName))
            {
                throw new ArgumentException("Service name is required.", nameof(request.ServiceName));
            }

            if (request.Amount <= 0)
            {
                throw new ArgumentException("Amount must be greater than zero.", nameof(request.Amount));
            }

            ServiceCharge? entity;

            if (request.Id.HasValue && request.Id != Guid.Empty)
            {
                entity = await _context.ServiceCharges
                    .FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken);
                
                if (entity == null)
                {
                    throw new KeyNotFoundException($"Service charge with ID '{request.Id}' not found.");
                }

                // Verify ownership
                if (entity.HospitalId != _context.UserContext.HospitalId)
                {
                    throw new UnauthorizedAccessException("You do not have permission to modify this service charge.");
                }
            }
            else
            {
                // Check for duplicates
                var duplicate = await _context.ServiceCharges
                    .FirstOrDefaultAsync(x => x.HospitalId == _context.UserContext.HospitalId &&
                                             x.Modality == request.Modality &&
                                             x.ServiceName == request.ServiceName, cancellationToken);
                
                if (duplicate != null)
                {
                    throw new InvalidOperationException($"Service charge for '{request.ServiceName}' in '{request.Modality}' already exists.");
                }

                entity = new ServiceCharge
                {
                    HospitalId = _context.UserContext.HospitalId
                };
                _context.ServiceCharges.Add(entity);
            }

            entity.Modality = request.Modality;
            entity.ServiceName = request.ServiceName;
            entity.Amount = request.Amount;
            entity.ReferralCutValue = request.ReferralCutValue;

            await _context.SaveChangesAsync(cancellationToken);

            return entity.Id;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to save service charge: {ex.Message}", ex);
        }
    }
}
