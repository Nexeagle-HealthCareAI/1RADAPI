using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Approvals.Queries.GetApprovals;

/// <summary>
/// Lists this hospital's approval requests for the Finance → Approvals page.
/// Defaults to PENDING; pass Status="ALL" to include reviewed history.
/// </summary>
public record GetApprovalsQuery : IRequest<List<ApprovalRequestDto>>
{
    public string? Status { get; init; } = "PENDING";
}

public class ApprovalRequestDto
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public Guid? InvoiceId { get; set; }
    public Guid? AppointmentId { get; set; }
    public string Payload { get; set; } = "{}";
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid RequestedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class GetApprovalsQueryHandler : IRequestHandler<GetApprovalsQuery, List<ApprovalRequestDto>>
{
    private readonly IApplicationDbContext _context;

    public GetApprovalsQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<List<ApprovalRequestDto>> Handle(GetApprovalsQuery request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        var status = (request.Status ?? "PENDING").Trim().ToUpperInvariant();

        var q = _context.ApprovalRequests
            .Where(a => a.HospitalId == hospitalId && a.DeletedAt == null);

        if (status != "ALL")
            q = q.Where(a => a.Status == status);

        return await q
            .OrderByDescending(a => a.CreatedAt)
            .Take(200)
            .Select(a => new ApprovalRequestDto
            {
                Id = a.Id,
                Type = a.Type,
                Title = a.Title,
                InvoiceId = a.InvoiceId,
                AppointmentId = a.AppointmentId,
                Payload = a.Payload,
                Reason = a.Reason,
                Status = a.Status,
                RequestedBy = a.RequestedBy,
                ReviewedBy = a.ReviewedBy,
                ReviewNote = a.ReviewNote,
                ReviewedAt = a.ReviewedAt,
                CreatedAt = a.CreatedAt,
            })
            .ToListAsync(ct);
    }
}
