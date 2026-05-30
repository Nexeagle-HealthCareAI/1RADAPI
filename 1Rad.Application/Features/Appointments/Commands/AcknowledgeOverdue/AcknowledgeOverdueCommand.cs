using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.AcknowledgeOverdue;

// Single endpoint for both ack + un-ack: pass acknowledged=true to mark seen,
// false to revert. Keeps the controller surface narrow and lets the bell UI
// toggle the same way regardless of direction.
public record AcknowledgeOverdueCommand(Guid AppointmentId, bool Acknowledged) : IRequest<bool>;

public class AcknowledgeOverdueCommandHandler : IRequestHandler<AcknowledgeOverdueCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public AcknowledgeOverdueCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(AcknowledgeOverdueCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        if (request.Acknowledged)
        {
            // Only stamp if not already acked — preserves the original ack
            // user + time as audit. Re-acking is a no-op.
            if (appointment.OverdueAcknowledgedAt == null)
            {
                appointment.OverdueAcknowledgedAt = DateTime.UtcNow;
                appointment.OverdueAcknowledgedBy = _context.UserContext.UserId;
            }
        }
        else
        {
            appointment.OverdueAcknowledgedAt = null;
            appointment.OverdueAcknowledgedBy = null;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
