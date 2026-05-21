using System.Text.Json;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.LeavePolicy.Commands.SaveLeavePolicy;

public class SaveLeavePolicyCommandHandler : IRequestHandler<SaveLeavePolicyCommand, (Guid PolicyId, string? Error)>
{
    private readonly IApplicationDbContext _context;

    public SaveLeavePolicyCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<(Guid PolicyId, string? Error)> Handle(SaveLeavePolicyCommand request, CancellationToken cancellationToken)
    {
        // Validate the JSON parses + contains a plain array of leave type objects.
        try
        {
            using var doc = JsonDocument.Parse(request.LeaveTypesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (Guid.Empty, "leaveTypesJson must be a JSON array.");
        }
        catch (JsonException)
        {
            return (Guid.Empty, "Invalid JSON in leaveTypesJson.");
        }

        var policy = await _context.HospitalLeavePolicies
            .FirstOrDefaultAsync(p => p.HospitalId == request.HospitalId, cancellationToken);

        if (policy == null)
        {
            policy = new HospitalLeavePolicy
            {
                HospitalId      = request.HospitalId,
                LeaveTypesJson  = request.LeaveTypesJson,
                UpdatedByUserId = request.UpdatedByUserId,
                UpdatedAt       = DateTime.UtcNow,
            };
            _context.HospitalLeavePolicies.Add(policy);
        }
        else
        {
            policy.LeaveTypesJson  = request.LeaveTypesJson;
            policy.UpdatedByUserId = request.UpdatedByUserId;
            policy.UpdatedAt       = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return (policy.PolicyId, null);
    }
}
