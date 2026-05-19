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

        appointment.ReportProgressStatus = request.ProgressStatus;
        appointment.DelayReason = request.DelayReason;

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
