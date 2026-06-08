using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Approvals.Queries.GetApprovalsCount;

/// <summary>
/// Lightweight pending-approvals count for the nav badge. Returns just an int
/// via CountAsync — no rows, user-name subqueries, or payloads. The badge used
/// to fetch the full (Take 200) list only to read its .length, which made the
/// per-admin poll needlessly heavy.
/// </summary>
public record GetApprovalsCountQuery(string? Status = "PENDING") : IRequest<int>;

public class GetApprovalsCountQueryHandler : IRequestHandler<GetApprovalsCountQuery, int>
{
    private readonly IApplicationDbContext _context;

    public GetApprovalsCountQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<int> Handle(GetApprovalsCountQuery request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var status = (request.Status ?? "PENDING").Trim().ToUpperInvariant();

        var q = _context.ApprovalRequests
            .Where(a => a.HospitalId == hospitalId && a.DeletedAt == null);

        if (status != "ALL")
            q = q.Where(a => a.Status == status);

        return await q.CountAsync(ct);
    }
}
