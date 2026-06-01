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
        // Try to parse as Guid first, then fallback to DisplayId
        _ = Guid.TryParse(request.Id, out var guidId);

        var appointment = await _context.Appointments
            .AsNoTracking()
            .Include(a => a.Patient)

            .GroupJoin(_context.Invoices,
                a => a.AppointmentId,
                i => i.AppointmentId,
                (a, invoices) => new { Appointment = a, Invoices = invoices })
            .SelectMany(x => x.Invoices.DefaultIfEmpty(),
                (x, invoice) => new { x.Appointment, Invoice = invoice })
            .Where(x => x.Appointment.AppointmentId == guidId || x.Appointment.DisplayId == request.Id)
            .Select(x => new AppointmentDto(
                x.Appointment.AppointmentId,
                x.Appointment.DisplayId ?? string.Empty,
                x.Appointment.PatientId,
                x.Appointment.Patient != null ? (x.Appointment.Patient.FullName ?? "Unknown") : "Unknown",
                x.Appointment.Mobile ?? string.Empty,
                x.Appointment.Patient != null ? (x.Appointment.Patient.Age ?? "0") : "0",
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
                x.Appointment.Priority ?? "ROUTINE",
                x.Appointment.ArrivedAt,
                x.Appointment.ScanStartedAt,
                x.Appointment.DeliveredAt,
                x.Appointment.LatestCommentAuthorName,
                x.Appointment.LatestCommentAt,
                x.Appointment.UpdatedAt,
                x.Appointment.DeletedAt,
                // Services is materialised by the second query below;
                // typed null here keeps EF's expression tree happy.
                (IReadOnlyList<AppointmentServiceDto>?)null
            ))

            .FirstOrDefaultAsync(cancellationToken);

        if (appointment == null) return null;

        // Attach service lines so the response shape matches GetAppointments.
        var lines = await _context.AppointmentServices
            .AsNoTracking()
            .Where(s => s.AppointmentId == appointment.AppointmentId && s.DeletedAt == null)
            .OrderBy(s => s.UpdatedAt)
            .Select(s => new AppointmentServiceDto(
                s.Id,
                s.ServiceName,
                s.Modality,
                s.Amount,
                s.ReferralCutValue,
                s.Status,
                s.ScanStartedAt,
                s.ScanCompletedAt,
                s.ReportedAt,
                s.DeliveredAt,
                s.CancelledAt,
                s.TechnicianId,
                s.ServiceChargeId,
                s.UpdatedAt
            ))
            .ToListAsync(cancellationToken);

        return appointment with { Services = lines };
    }
}
