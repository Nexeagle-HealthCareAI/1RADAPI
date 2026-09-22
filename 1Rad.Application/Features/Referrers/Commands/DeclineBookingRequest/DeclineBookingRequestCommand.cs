using System;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Commands.DeclineBookingRequest;

/// <summary>Staff declines a doctor-portal booking request (wrong number, duplicate, needs a call
/// to sort out the timing, etc.). The doctor sees the reason next to it in their own portal.</summary>
public record DeclineBookingRequestCommand(Guid Id, string? Reason) : IRequest<bool>;

public class DeclineBookingRequestCommandHandler : IRequestHandler<DeclineBookingRequestCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public DeclineBookingRequestCommandHandler(IApplicationDbContext context) => _context = context;

    public async Task<bool> Handle(DeclineBookingRequestCommand request, CancellationToken ct)
    {
        var entry = await _context.ReferralBookingRequests
            .FirstOrDefaultAsync(r => r.Id == request.Id, ct);
        if (entry == null) throw new NotFoundException("Booking request", request.Id);

        if (entry.Status != "PENDING")
            throw new BusinessRuleViolationException($"This request has already been {entry.Status.ToLowerInvariant()} - it can't be declined now.");

        entry.Status = "DECLINED";
        entry.DeclineReason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        entry.DecidedByUserId = _context.UserContext.UserId;
        entry.DecidedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return true;
    }
}
