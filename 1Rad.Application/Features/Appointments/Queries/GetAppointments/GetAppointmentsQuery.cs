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
        try
        {
            // Validate hospital context
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new List<AppointmentDto>();
            }

            var query = _context.Appointments
                .Include(a => a.Patient)
                .Where(a => a.HospitalId == _context.UserContext.HospitalId)
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
                    a.Patient != null ? (a.Patient.Age?.ToString() ?? "0") : "0",
                    a.Patient != null ? (a.Patient.Gender ?? "Unknown") : "Unknown",
                    a.Patient != null ? (a.Patient.PatientIdentifier ?? string.Empty) : string.Empty,
                    a.Service,
                    a.Modality,
                    a.DateTime,
                    a.Type,
                    a.Doctor ?? string.Empty,
                    a.Status,
                    a.ReferredBy ?? string.Empty,
                    a.ReferredContact ?? string.Empty,
                    a.Notes ?? string.Empty,
                    a.TechnicianComments ?? string.Empty,
                    a.TechnicianId,
                    a.ScannedAt
                ))
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve appointments: {ex.Message}", ex);
        }
    }
}
