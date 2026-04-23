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
                a.DisplayId,
                a.PatientId,
                a.PatientName,
                a.Mobile,
                a.Patient.Age,
                a.Patient.Gender,
                a.Patient.PatientIdentifier,
                a.Service,
                a.Modality,
                a.DateTime,
                a.Type,
                a.Doctor,
                a.Status,
                a.ReferredBy,
                a.ReferredContact,
                a.Notes,
                a.TechnicianComments,
                a.TechnicianId,
                a.ScannedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);

        return appointment;
    }
}
