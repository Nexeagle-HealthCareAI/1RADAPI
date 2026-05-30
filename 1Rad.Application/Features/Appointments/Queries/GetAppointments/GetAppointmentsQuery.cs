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
                query = query.Where(x => x.Appointment.Status == request.Status);
            }


            if (!string.IsNullOrEmpty(request.SearchQuery))
            {
                var search = request.SearchQuery.ToLower().Trim();
                
                if (Guid.TryParse(search, out Guid parsedGuid))
                {
                    query = query.Where(x => x.Appointment.PatientId == parsedGuid || x.Appointment.AppointmentId == parsedGuid);
                }
                else
                {
                    query = query.Where(x => 
                        (x.Appointment.Patient != null && x.Appointment.Patient.FullName != null && x.Appointment.Patient.FullName.ToLower().Contains(search)) || 
                        (x.Appointment.Mobile != null && x.Appointment.Mobile.Contains(search)) || 
                        (x.Appointment.DisplayId != null && x.Appointment.DisplayId.ToLower().Contains(search)) ||
                        (x.Appointment.Patient != null && x.Appointment.Patient.PatientIdentifier != null && x.Appointment.Patient.PatientIdentifier.ToLower().Contains(search)));
                }
            }

            // Project to DTO directly in the query to avoid entity materialization issues.
            // Worklist sort: STAT (0) → URGENT (1) → ROUTINE (2), then DateTime
            // ASC. Translated to a CASE in SQL via the IX_Appointments_HospitalId_
            // Priority_DateTime index so STATs float to the top regardless of
            // their scheduled time.
            var appointments = await query
                .OrderBy(x =>
                    x.Appointment.Priority == "STAT"   ? 0 :
                    x.Appointment.Priority == "URGENT" ? 1 : 2)
                .ThenBy(x => x.Appointment.DateTime)
                .Select(x => new AppointmentDto(
                    x.Appointment.AppointmentId,
                    x.Appointment.DisplayId ?? string.Empty,
                    x.Appointment.PatientId,
                    x.Appointment.Patient != null ? (x.Appointment.Patient.FullName ?? "Unknown") : "Unknown",
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
                    x.Invoice != null ? x.Invoice.ReferralCutValue : 0,
                    _context.StudyAssets.Count(sa => sa.AppointmentId == x.Appointment.AppointmentId),
                    _context.DiagnosticReports
                        .Where(dr => dr.AppointmentId == x.Appointment.AppointmentId)
                        .Select(dr => dr.Impression)
                        .FirstOrDefault(),
                    x.Appointment.DailyTokenNumber,
                    x.Appointment.DelayReason,
                    x.Appointment.ReportProgressStatus ?? "NOT_STARTED",
                    x.Appointment.Priority ?? "ROUTINE"
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
