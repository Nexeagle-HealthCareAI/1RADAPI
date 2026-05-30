using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetOverdueAppointments;

// Compact projection meant for the nav-bar bell + dashboard pulse, NOT the
// full worklist. Stays small (Mb-level even at 100k rows) by selecting only
// the fields the alert UI actually needs.
public record OverdueAppointmentDto(
    Guid AppointmentId,
    string DisplayId,
    string PatientName,
    string Modality,
    string Priority,
    string Status,
    DateTime ArrivedAt,
    int ElapsedMinutes
);

public record GetOverdueAppointmentsQuery(int ThresholdMinutes) : IRequest<List<OverdueAppointmentDto>>;

public class GetOverdueAppointmentsQueryHandler
    : IRequestHandler<GetOverdueAppointmentsQuery, List<OverdueAppointmentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetOverdueAppointmentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<OverdueAppointmentDto>> Handle(
        GetOverdueAppointmentsQuery request,
        CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            return new List<OverdueAppointmentDto>();

        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc.AddMinutes(-request.ThresholdMinutes);

        // Active = arrived but not delivered and not cancelled. The filtered
        // index IX_Appointments_Overdue_Active covers this exact predicate so
        // even at scale the query is a small filtered seek, not a full scan.
        // Pull the small set of overdue rows first (one filtered-index seek),
        // then project + compute ElapsedMinutes in memory. The result count
        // is bounded by the number of patients on premises >3h — tiny.
        var rows = await _context.Appointments
            .AsNoTracking()
            .Where(a => a.HospitalId == _context.UserContext.HospitalId
                     && a.ArrivedAt != null
                     && a.DeliveredAt == null
                     && a.Status != "CANCELLED"
                     && a.ArrivedAt <= cutoffUtc)
            .Include(a => a.Patient)
            .OrderBy(a => a.ArrivedAt) // longest-waiting first
            .Select(a => new
            {
                a.AppointmentId,
                a.DisplayId,
                PatientName = a.Patient != null ? a.Patient.FullName : null,
                a.Modality,
                a.Priority,
                a.Status,
                ArrivedAt = a.ArrivedAt!.Value,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(a => new OverdueAppointmentDto(
            a.AppointmentId,
            a.DisplayId ?? string.Empty,
            a.PatientName ?? "Unknown",
            a.Modality ?? string.Empty,
            a.Priority ?? "ROUTINE",
            a.Status ?? "BOOKED",
            a.ArrivedAt,
            (int)Math.Round((nowUtc - a.ArrivedAt).TotalMinutes)
        )).ToList();
    }
}
