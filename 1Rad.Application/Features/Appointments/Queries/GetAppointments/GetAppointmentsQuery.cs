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
                .GroupJoin(_context.Invoices,
                    a => a.AppointmentId,
                    i => i.AppointmentId,
                    (a, invoices) => new { Appointment = a, Invoices = invoices })
                .SelectMany(x => x.Invoices.DefaultIfEmpty(),
                    (x, invoice) => new { x.Appointment, Invoice = invoice })
                .Where(x => x.Appointment.HospitalId == _context.UserContext.HospitalId)
                .AsNoTracking();

            if (!string.IsNullOrEmpty(request.Status) && request.Status != "ALL")
            {
                query = query.Where(a => a.Status == request.Status);
            }

            if (!string.IsNullOrEmpty(request.SearchQuery))
            {
                var search = request.SearchQuery.ToLower();
                query = query.Where(x => 
                    (x.Appointment.PatientName != null && x.Appointment.PatientName.ToLower().Contains(search)) || 
                    (x.Appointment.Mobile != null && x.Appointment.Mobile.Contains(search)) || 
                    (x.Appointment.DisplayId != null && x.Appointment.DisplayId.ToLower().Contains(search)));
            }

            // Project to DTO directly in the query to avoid entity materialization issues
            var appointments = await query
                .OrderByDescending(x => x.Appointment.DateTime)
                .Select(x => new AppointmentDto(
                    x.Appointment.AppointmentId,
                    x.Appointment.DisplayId ?? string.Empty,
                    x.Appointment.PatientId,
                    x.Appointment.PatientName ?? "Unknown",
                    x.Appointment.Mobile ?? string.Empty,
                    x.Appointment.Patient != null ? (x.Appointment.Patient.Age != null ? x.Appointment.Patient.Age.ToString() : "0") : "0",
                    x.Appointment.Patient != null ? (x.Appointment.Patient.Gender ?? "Unknown") : "Unknown",
                    x.Appointment.Patient != null ? (x.Appointment.Patient.PatientIdentifier ?? string.Empty) : string.Empty,
                    x.Appointment.Service ?? string.Empty,
                    x.Appointment.Modality ?? string.Empty,
                    x.Appointment.DateTime,
                    x.Appointment.Type ?? "BOOKED",
                    x.Appointment.Doctor ?? string.Empty,
                    x.Appointment.Status ?? "BOOKED",
                    x.Appointment.ReferredBy ?? string.Empty,
                    x.Appointment.ReferredContact ?? string.Empty,
                    x.Appointment.Notes ?? string.Empty,
                    x.Appointment.TechnicianComments ?? string.Empty,
                    x.Appointment.TechnicianId,
                    x.Appointment.ScannedAt,
                    x.Invoice != null ? x.Invoice.TotalAmount : 0,
                    x.Invoice != null ? x.Invoice.ReferralCutType ?? "PERCENTAGE" : "PERCENTAGE",
                    x.Invoice != null ? x.Invoice.ReferralCutValue : 0
                ))
                .ToListAsync(cancellationToken);

            return appointments;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve appointments: {ex.Message}", ex);
        }
    }
}
