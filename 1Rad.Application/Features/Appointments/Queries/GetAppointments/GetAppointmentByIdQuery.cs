using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Threading;
using System.Threading.Tasks;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public record GetAppointmentByIdQuery(string Id) : IRequest<AppointmentDto?>;

public class GetAppointmentByIdQueryHandler : IRequestHandler<GetAppointmentByIdQuery, AppointmentDto?>
{
    private readonly IApplicationDbContext _context;

    public GetAppointmentByIdQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<AppointmentDto?> Handle(GetAppointmentByIdQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Appointments.AsNoTracking();

        // Try to parse as Guid first, then fallback to DisplayId
        _ = Guid.TryParse(request.Id, out var guidId);

        var appointment = await query
            .Where(a => a.AppointmentId == guidId || a.DisplayId == request.Id)
            .Select(a => new AppointmentDto(
                a.AppointmentId,
                a.DisplayId ?? string.Empty,
                a.PatientId,
                a.PatientName ?? "Unknown",
                a.Mobile ?? string.Empty,
                a.Patient != null ? (a.Patient.Age ?? "0") : "0",
                a.Patient != null ? (a.Patient.Gender ?? "Unknown") : "Unknown",
                a.Patient != null ? (a.Patient.PatientIdentifier ?? string.Empty) : string.Empty,
                a.Service ?? string.Empty,
                a.Modality ?? string.Empty,
                a.DateTime,
                a.Type ?? "BOOKED",
                a.Doctor ?? string.Empty,
                a.Status ?? "BOOKED",
                a.ReferredBy ?? string.Empty,
                a.ReferredContact ?? string.Empty,
                a.Notes ?? string.Empty,
                a.TechnicianComments ?? string.Empty,
                a.TechnicianId,
                a.ScannedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);

        return appointment;
    }
}
