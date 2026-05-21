using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.LeavePolicy.Queries.GetLeavePolicy;

public class GetLeavePolicyQueryHandler : IRequestHandler<GetLeavePolicyQuery, LeavePolicyDto>
{
    private readonly IApplicationDbContext _context;

    public GetLeavePolicyQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<LeavePolicyDto> Handle(GetLeavePolicyQuery request, CancellationToken cancellationToken)
    {
        var policy = await _context.HospitalLeavePolicies
            .FirstOrDefaultAsync(p => p.HospitalId == request.HospitalId, cancellationToken);

        // No defaults — hospital must configure its own leave types via the Leave Policy tab.
        // Returning an empty array signals the UI to show the "no policy configured" state.
        if (policy == null)
            return new LeavePolicyDto(null, "[]", null);

        return new LeavePolicyDto(policy.PolicyId, policy.LeaveTypesJson, policy.UpdatedAt);
    }
}
