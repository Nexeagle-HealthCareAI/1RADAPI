using MediatR;
using Microsoft.EntityFrameworkCore;
using _1Rad.Application.Interfaces;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateReportProgress;

public record UpdateReportProgressCommand(
    Guid AppointmentId, 
    string ProgressStatus, 
    string? DelayReason
) : IRequest<bool>;

public class UpdateReportProgressCommandHandler : IRequestHandler<UpdateReportProgressCommand, bool>
{
    private readonly IApplicationDbContext _context;

    public UpdateReportProgressCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<bool> Handle(UpdateReportProgressCommand request, CancellationToken cancellationToken)
    {
        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return false;

        // Capture the delivery timestamp on the FIRST transition into
        // DELIVERED. Idempotent on purpose — re-marking as delivered (or a
        // future correction) won't overwrite the original delivery time, so
        // turnaround analytics stay honest.
        if (request.ProgressStatus == "DELIVERED" && appointment.DeliveredAt == null)
        {
            appointment.DeliveredAt = DateTime.UtcNow;
        }

        appointment.ReportProgressStatus = request.ProgressStatus;
        appointment.DelayReason = request.DelayReason;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
