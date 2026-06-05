using MediatR;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;

namespace _1Rad.Application.Features.Approvals.Commands.CreateApprovalRequest;

/// <summary>
/// Submits a sensitive change for admin sign-off. The change is NOT applied
/// here — it sits PENDING until an admin approves it on Finance → Approvals.
/// </summary>
public record CreateApprovalRequestCommand : IRequest<Guid>
{
    public string Type { get; init; } = string.Empty;   // EDIT_PAYMENT | CANCEL_APPOINTMENT | CHANGE_REFERRER
    public string Title { get; init; } = string.Empty;
    public Guid? InvoiceId { get; init; }
    public Guid? AppointmentId { get; init; }
    public string Payload { get; init; } = "{}";
    public string Reason { get; init; } = string.Empty;
}

public class CreateApprovalRequestCommandHandler : IRequestHandler<CreateApprovalRequestCommand, Guid>
{
    private readonly IApplicationDbContext _context;

    public CreateApprovalRequestCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<Guid> Handle(CreateApprovalRequestCommand request, CancellationToken ct)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context is required.");

        var allowed = new[] { "EDIT_PAYMENT", "CANCEL_APPOINTMENT", "CHANGE_REFERRER", "MARK_FREE", "UNPAY_COMMISSION" };
        var type = (request.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (!allowed.Contains(type))
            throw new InvalidOperationException($"Unknown approval type '{request.Type}'.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("A reason is required for this request.");

        var entity = new ApprovalRequest
        {
            HospitalId = _context.UserContext.HospitalId,
            Type = type,
            Title = (request.Title ?? string.Empty).Trim(),
            InvoiceId = request.InvoiceId,
            AppointmentId = request.AppointmentId,
            Payload = string.IsNullOrWhiteSpace(request.Payload) ? "{}" : request.Payload,
            Reason = request.Reason.Trim(),
            Status = "PENDING",
            RequestedBy = _context.UserContext.UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _context.ApprovalRequests.Add(entity);
        await _context.SaveChangesAsync(ct);
        return entity.Id;
    }
}
