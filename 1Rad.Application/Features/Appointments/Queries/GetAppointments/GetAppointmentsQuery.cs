using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

public record GetAppointmentsQuery(string? SearchQuery = null, string? Status = null) : IRequest<List<AppointmentDto>>;

public class GetAppointmentsQueryHandler : IRequestHandler<GetAppointmentsQuery, List<AppointmentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetAppointmentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<AppointmentDto>> Handle(GetAppointmentsQuery request, CancellationToken cancellationToken)
    {
        var query = _context.Appointments
            .Include(a => a.Patient)
            .AsNoTracking();

        if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
        {
            query = query.Where(a => a.Status == request.Status);
        }

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(a => 
                (a.PatientName != null && a.PatientName.ToLower().Contains(search)) || 
                (a.Mobile != null && a.Mobile.Contains(search)) || 
                (a.DisplayId != null && a.DisplayId.ToLower().Contains(search)));
        }

        return await query
            .OrderByDescending(a => a.DateTime)
            .Select(a => new AppointmentDto(
                a.AppointmentId,
                a.DisplayId ?? string.Empty,
                a.PatientId,
                a.PatientName ?? "Unknown",
                a.Mobile ?? string.Empty,
                a.Patient.Age ?? "0",
                a.Patient.Gender ?? "Unknown",
                a.Patient.PatientIdentifier ?? string.Empty,
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
            .ToListAsync(cancellationToken);
    }
}
