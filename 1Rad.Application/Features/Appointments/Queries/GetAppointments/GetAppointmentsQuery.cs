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
        var query = _context.Appointments.AsNoTracking();

        if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
        {
            query = query.Where(a => a.Status == request.Status);
        }

        if (!string.IsNullOrEmpty(request.SearchQuery))
        {
            var search = request.SearchQuery.ToLower();
            query = query.Where(a => 
                a.PatientName.ToLower().Contains(search) || 
                a.Mobile.Contains(search) || 
                a.DisplayId.ToLower().Contains(search));
        }

        return await query
            .OrderByDescending(a => a.DateTime)
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
            .ToListAsync(cancellationToken);
    }
}
