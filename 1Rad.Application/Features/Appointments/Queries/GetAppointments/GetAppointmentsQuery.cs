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
    bool IncludeDeleted = false,
    DateTime? StartDate = null,
    int PageSize = 0,
    string? Cursor = null
) : IRequest<PagedAppointmentResult>;

public class GetAppointmentsQueryHandler : IRequestHandler<GetAppointmentsQuery, PagedAppointmentResult>
{
    private readonly IApplicationDbContext _context;

    public GetAppointmentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<PagedAppointmentResult> Handle(GetAppointmentsQuery request, CancellationToken cancellationToken)
    {
        try
        {
            if (_context.UserContext.HospitalId == Guid.Empty)
            {
                return new PagedAppointmentResult();
            }

            var query = _context.Appointments
                .AsNoTracking()
                .ApplyWorklistFilters(request, _context.UserContext.HospitalId);

            // ── Keyset cursor decode ─────────────────────────────────────────
            bool usePaging = request.PageSize > 0 && !request.IncludeDeleted && !request.UpdatedAfter.HasValue;
            DateTime? cursorDate = null;
            Guid? cursorId = null;
            if (usePaging && !string.IsNullOrEmpty(request.Cursor))
            {
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(request.Cursor));
                    var parts = decoded.Split('|');
                    if (parts.Length == 2)
                    {
                        cursorDate = new DateTime(long.Parse(parts[0]), DateTimeKind.Utc);
                        cursorId = Guid.Parse(parts[1]);
                    }
                }
                catch { /* malformed cursor — treat as first page */ }
            }

            if (usePaging && cursorDate.HasValue && cursorId.HasValue)
            {
                query = query.Where(a =>
                    a.DateTime < cursorDate.Value ||
                    (a.DateTime == cursorDate.Value && a.AppointmentId < cursorId.Value));
            }

            int totalCount = 0;
            if (usePaging)
            {
                totalCount = await query.CountAsync(cancellationToken);
            }

            int takeCount = usePaging ? request.PageSize + 1 : (request.IncludeDeleted ? 100_000 : 200);

            var appointmentQuery = query
                .OrderByDescending(a => a.DateTime)
                .ThenByDescending(a => a.AppointmentId)
                .Take(takeCount)
                .Select(a => new {
                    Appointment = a,
                    Invoice = _context.Invoices.FirstOrDefault(i => i.AppointmentId == a.AppointmentId)
                });

            var rawResults = await appointmentQuery.ToListAsync(cancellationToken);

            var appointmentIds = rawResults.Select(x => x.Appointment.AppointmentId).ToList();

            // Batched StudyAsset counts
            var assetCountByAppointment = new Dictionary<Guid, int>();
            if (appointmentIds.Any())
            {
                assetCountByAppointment = (await _context.StudyAssets
                    .AsNoTracking()
                    .Where(sa => sa.AppointmentId != null && appointmentIds.Contains(sa.AppointmentId.Value))
                    .GroupBy(sa => sa.AppointmentId!.Value)
                    .Select(g => new { AppointmentId = g.Key, Count = g.Count() })
                    .ToListAsync(cancellationToken))
                    .ToDictionary(x => x.AppointmentId, x => x.Count);
            }

            var summaryList = rawResults.Select(x => new AppointmentSummaryDto(
                x.Appointment.AppointmentId,
                x.Appointment.DisplayId ?? string.Empty,
                x.Appointment.PatientId,
                x.Appointment.Patient?.FullName ?? "Unknown",
                x.Appointment.Mobile ?? string.Empty,
                x.Appointment.Patient?.Age?.ToString() ?? "0",
                x.Appointment.Patient?.Gender ?? "Unknown",
                x.Appointment.Patient?.PatientIdentifier ?? string.Empty,
                x.Appointment.Service ?? string.Empty,
                x.Appointment.Modality ?? string.Empty,
                x.Appointment.DateTime,
                x.Appointment.Type ?? "BOOKED",
                x.Appointment.Doctor ?? string.Empty,
                x.Appointment.Status ?? "BOOKED",
                x.Appointment.ReferredBy ?? string.Empty,
                x.Appointment.ReferredContact ?? string.Empty,
                x.Appointment.DailyTokenNumber,
                x.Appointment.DelayReason,
                x.Appointment.ReportProgressStatus ?? "NOT_STARTED",
                x.Appointment.Priority ?? "ROUTINE",
                x.Appointment.ArrivedAt,
                x.Appointment.ScanStartedAt,
                x.Appointment.DeliveredAt,
                x.Appointment.UpdatedAt,
                x.Appointment.DeletedAt,
                x.Invoice?.TotalAmount ?? 0,
                x.Invoice?.ReferralCutValue ?? 0,
                assetCountByAppointment.GetValueOrDefault(x.Appointment.AppointmentId, 0)
            )).ToList();

            string? nextCursor = null;
            if (usePaging && summaryList.Count() > request.PageSize)
            {
                summaryList.RemoveAt(summaryList.Count() - 1);
                var last = rawResults[summaryList.Count()].Appointment;
                var raw = $"{last.DateTime.Ticks}|{last.AppointmentId}";
                nextCursor = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
            }

            return new PagedAppointmentResult
            {
                Items = summaryList,
                NextCursor = nextCursor,
                TotalCount = usePaging ? totalCount : summaryList.Count(),
                IsPaged = usePaging
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to retrieve appointments: {ex.Message}", ex);
        }
    }
}
