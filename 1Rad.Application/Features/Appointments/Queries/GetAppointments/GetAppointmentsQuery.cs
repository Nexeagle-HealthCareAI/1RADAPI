using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointments;

// UpdatedAfter + IncludeDeleted are the sync-engine knobs added in Phase B1.
//   • UpdatedAfter: returns only rows whose UpdatedAt > value. Lets a client
//     that pulled at T+0 ask "what's changed since then" at T+30s and get
//     only the delta — not the full worklist every time.
//   • IncludeDeleted: by default the soft-deleted rows are hidden so the
//     existing online UX is unchanged. The sync engine flips this on so it
//     can apply tombstones to the local cache.
public record GetAppointmentsQuery(
    string? SearchQuery = null,
    string? Status = null,
    DateTime? UpdatedAfter = null,
    bool IncludeDeleted = false
) : IRequest<List<AppointmentDto>>;

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

            // Tombstone filter: hide soft-deleted rows from the everyday
            // worklist UI, surface them to the sync engine when it asks.
            if (!request.IncludeDeleted)
            {
                query = query.Where(x => x.Appointment.DeletedAt == null);
            }

            // Delta-fetch — runs against IX_Appointments_Hospital_UpdatedAt
            // (migration 47) so this is a small index range scan even at
            // worklists with years of history. The frontend Sync Engine
            // sends the value it received as the highest UpdatedAt from
            // the previous pull, NOT its local clock.
            if (request.UpdatedAfter.HasValue)
            {
                var since = request.UpdatedAfter.Value;
                query = query.Where(x => x.Appointment.UpdatedAt > since);
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
            // Capture the (filtered, sorted) appointment IDs so we can run
            // a single batched second query for services that scopes the
            // sync delta / search / hospital constraints the same way the
            // main projection does. Keeping it as a subquery instead of a
            // separate .Select() means EF emits one round trip for the
            // service fetch regardless of page size.
            var appointmentIds = query.Select(x => x.Appointment.AppointmentId);

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
                    x.Appointment.Priority ?? "ROUTINE",
                    x.Appointment.ArrivedAt,
                    x.Appointment.ScanStartedAt,
                    x.Appointment.DeliveredAt,
                    x.Appointment.LatestCommentAuthorName,
                    x.Appointment.LatestCommentAt,
                    x.Appointment.UpdatedAt,
                    x.Appointment.DeletedAt,
                    // Services is materialised separately below (a single
                    // batched second query) — projecting a typed null
                    // here keeps EF happy (it can't translate optional
                    // ctor args otherwise) and reserves the slot.
                    (IReadOnlyList<AppointmentServiceDto>?)null,
                    // ReferringDoctorName — only resolved on the single-record
                    // (reporting) fetch; the worklist doesn't need it.
                    (string?)null,
                    x.Appointment.SupportedByDoctor
                ))
                .ToListAsync(cancellationToken);

            // ── Batched service fetch ─────────────────────────────────
            // Second round trip: pull every AppointmentService row whose
            // parent is in the result set. Soft-deleted rows are excluded
            // unless the caller asked for tombstones too. Group by
            // AppointmentId so we can rewrite each DTO with its lines.
            var serviceQuery = _context.AppointmentServices
                .AsNoTracking()
                .Where(s => appointmentIds.Contains(s.AppointmentId));

            if (!request.IncludeDeleted)
            {
                serviceQuery = serviceQuery.Where(s => s.DeletedAt == null);
            }

            var services = await serviceQuery
                .OrderBy(s => s.UpdatedAt)
                .Select(s => new
                {
                    s.AppointmentId,
                    Dto = new AppointmentServiceDto(
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
                        s.UpdatedAt,
                        s.TechnicianComments
                    )
                })
                .ToListAsync(cancellationToken);

            var servicesByAppointment = services
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AppointmentServiceDto>)g.Select(x => x.Dto).ToList());

            // Reattach to each DTO. Records are immutable so we `with`-clone
            // — cheap because the underlying string/scalar fields are
            // pass-through references.
            for (int i = 0; i < appointments.Count; i++)
            {
                if (servicesByAppointment.TryGetValue(appointments[i].AppointmentId, out var lines))
                {
                    appointments[i] = appointments[i] with { Services = lines };
                }
                else
                {
                    // Visit has no service rows (shouldn't happen after
                    // migration 57's backfill, but defensive). Surface an
                    // empty list so frontends can rely on `services` being
                    // non-null on responses from this server build.
                    appointments[i] = appointments[i] with { Services = System.Array.Empty<AppointmentServiceDto>() };
                }
            }

            return appointments;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve appointments: {ex.Message}", ex);
        }
    }
}
