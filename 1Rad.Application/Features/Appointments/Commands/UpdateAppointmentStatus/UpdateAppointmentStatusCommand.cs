using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentStatus;

public record UpdateAppointmentStatusCommand(Guid AppointmentId, string Status) : IRequest<bool>;

public class UpdateAppointmentStatusCommandHandler : IRequestHandler<UpdateAppointmentStatusCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateAppointmentStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateAppointmentStatusCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        // Normalize to uppercase to match backend-set statuses (REPORTED, SCANNED, IN_PROGRESS, etc.)
        appointment.Status = request.Status.ToUpperInvariant();
        await _context.SaveChangesAsync(cancellationToken);

        return true;
    }
}
