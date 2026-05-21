using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.LeavePolicy.Queries.GetLeavePolicy;

public class GetLeavePolicyQueryHandler : IRequestHandler<GetLeavePolicyQuery, LeavePolicyDto>
{
    // Sensible defaults for a brand-new hospital. Returned (without persistence)
    // when no row exists yet, so the UI always has something to render.
    private const string DefaultJson = "[" +
        "{\"id\":\"sick\",\"name\":\"Sick Leave\",\"annualQuota\":6,\"isPaid\":true,\"color\":\"#dc2626\"}," +
        "{\"id\":\"casual\",\"name\":\"Casual Leave\",\"annualQuota\":6,\"isPaid\":true,\"color\":\"#0891b2\"}," +
        "{\"id\":\"earned\",\"name\":\"Earned Leave\",\"annualQuota\":12,\"isPaid\":true,\"color\":\"#16a34a\"}]";

    private readonly IApplicationDbContext _context;

    public GetLeavePolicyQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<LeavePolicyDto> Handle(GetLeavePolicyQuery request, CancellationToken cancellationToken)
    {
        var policy = await _context.HospitalLeavePolicies
            .FirstOrDefaultAsync(p => p.HospitalId == request.HospitalId, cancellationToken);

        if (policy == null)
            return new LeavePolicyDto(null, DefaultJson, null);

        return new LeavePolicyDto(policy.PolicyId, policy.LeaveTypesJson, policy.UpdatedAt);
    }
}
