using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceStatus;

public class UpdateAppointmentServiceStatusResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool NotAllowed { get; set; }

    // Echo back so the client can update its local cache without a refetch.
    public Guid? AppointmentServiceId { get; set; }
    public string? ServiceStatus { get; set; }
    public DateTime? ServiceScanStartedAt { get; set; }
    public DateTime? ServiceScanCompletedAt { get; set; }
    public DateTime? ServiceReportedAt { get; set; }
    public DateTime? ServiceDeliveredAt { get; set; }
    public DateTime? ServiceCancelledAt { get; set; }
    public string? AppointmentStatus { get; set; }
    public DateTime? AppointmentScanStartedAt { get; set; }
    public DateTime? AppointmentScannedAt { get; set; }
    public DateTime? AppointmentDeliveredAt { get; set; }
}

/// <summary>
/// Per-service status transition.
///
/// One visit can have many service lines (X-ray + CT + USG). Each line
/// is independently scanned and reported, so each gets its own status
/// transition. This command updates the single AppointmentService row
/// and recomputes the parent Appointment's rollup so the worklist's
/// "READY / SCANNING / SCHEDULED" pill and the TAT clocks stay correct.
///
/// Rollup policy:
///   • ScanStartedAt (parent) = MIN of services' ScanStartedAt.
///   • ScannedAt     (parent) = MAX of services' ScanCompletedAt, but
///                              only stamped when EVERY live service
///                              has been scanned (so the bell + scan→
///                              delivery TAT only fires once the visit
///                              is fully acquired).
///   • DeliveredAt   (parent) = MAX of services' DeliveredAt, only
///                              stamped when every live service has
///                              been delivered.
///   • Status        (parent) = "scanned" when every live service is
///                              at-or-past SCANNED, "in_progress" if
///                              any service has started, otherwise
///                              unchanged.
/// </summary>
public record UpdateAppointmentServiceStatusCommand(
    Guid AppointmentId,
    Guid AppointmentServiceId,
    string Status
) : IRequest<UpdateAppointmentServiceStatusResult>;

public class UpdateAppointmentServiceStatusCommandHandler
    : IRequestHandler<UpdateAppointmentServiceStatusCommand, UpdateAppointmentServiceStatusResult>
{
    private readonly IApplicationDbContext _context;

    // Status whitelist — keeps the table's Status column to known values.
    // IN_PROGRESS marks "scan has started" (technician opened the room);
    // IN_MID marks "scan is half-way done" (contrast injected, second
    // pass running). Both stamp ScanStartedAt without ScanCompletedAt,
    // so the visit-level "everyone scanned?" rollup still treats the
    // line as outstanding work.
    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT_STARTED", "IN_PROGRESS", "IN_MID", "SCANNED", "REPORTED", "DELIVERED", "CANCELLED"
    };

    public UpdateAppointmentServiceStatusCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UpdateAppointmentServiceStatusResult> Handle(
        UpdateAppointmentServiceStatusCommand request,
        CancellationToken cancellationToken)
    {
        var newStatus = (request.Status ?? string.Empty).Trim().ToUpperInvariant();
        if (!ValidStatuses.Contains(newStatus))
        {
            return new UpdateAppointmentServiceStatusResult
            {
                Success = false,
                Message = $"Unknown service status '{request.Status}'.",
                NotAllowed = true,
            };
        }

        var service = await _context.AppointmentServices
            .FirstOrDefaultAsync(s =>
                s.Id == request.AppointmentServiceId &&
                s.AppointmentId == request.AppointmentId,
                cancellationToken);

        if (service == null)
        {
            return new UpdateAppointmentServiceStatusResult
            {
                Success = false,
                Message = "Service not found on this appointment.",
            };
        }

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null)
        {
            return new UpdateAppointmentServiceStatusResult
            {
                Success = false,
                Message = "Appointment not found.",
            };
        }

        // Arrival gate — a service line can't be advanced (scanning, reporting,
        // delivery) until the patient has actually arrived. Cancelling or
        // resetting to NOT_STARTED is still allowed.
        var ADVANCE_STATUSES = new[] { "IN_PROGRESS", "IN_MID", "SCANNED", "REPORTED", "DELIVERED" };
        if (ADVANCE_STATUSES.Contains(newStatus))
        {
            var curStatus = (appointment.Status ?? string.Empty).ToLowerInvariant();
            var notArrivedYet = appointment.ArrivedAt == null &&
                (curStatus == string.Empty || curStatus == "scheduled" || curStatus == "booked" || curStatus == "future");
            if (notArrivedYet)
            {
                return new UpdateAppointmentServiceStatusResult
                {
                    Success = true,
                    NotAllowed = true,
                    Message = "The patient has not arrived yet. Mark the patient as arrived before updating the study status."
                };
            }
        }

        var nowUtc = DateTime.UtcNow;

        // Per-service timestamp rules. Idempotent — re-marking SCANNED
        // doesn't move the timestamp forward, mirroring how the
        // appointment-level UpdateAppointmentStatusCommand handles the
        // visit-level milestones.
        switch (newStatus)
        {
            case "IN_PROGRESS":
            case "IN_MID":
                // Intermediate stages — scan has started, not yet
                // complete. Both stamp ScanStartedAt only;
                // ScanCompletedAt stays null so the SCANNED → REPORTED
                // rollup still treats this line as outstanding work.
                // IN_PROGRESS = scan begun. IN_MID = halfway done.
                if (service.ScanStartedAt   == null) service.ScanStartedAt   = nowUtc;
                break;
            case "SCANNED":
                // First-time arrival into SCANNED stamps both the start
                // and the completion if neither is set. If only the
                // start is set (very common — the tech opens the
                // workspace which marks IN_PROGRESS on the parent and
                // we don't have a per-service "start" button yet), set
                // the completion now.
                if (service.ScanStartedAt   == null) service.ScanStartedAt   = nowUtc;
                if (service.ScanCompletedAt == null) service.ScanCompletedAt = nowUtc;
                break;
            case "REPORTED":
                if (service.ScanCompletedAt == null) service.ScanCompletedAt = nowUtc;
                // ReportedAt anchors the "Awaiting delivery for X" pill.
                // Backfilled to UpdatedAt by migration 58 for legacy rows.
                if (service.ReportedAt      == null) service.ReportedAt      = nowUtc;
                break;
            case "DELIVERED":
                if (service.ScanCompletedAt == null) service.ScanCompletedAt = nowUtc;
                // If we somehow skipped REPORTED (manual override) we
                // still want a sensible ReportedAt so the timeline
                // looks consistent — stamp it one tick before delivery.
                if (service.ReportedAt      == null) service.ReportedAt      = nowUtc.AddSeconds(-1);
                if (service.DeliveredAt     == null) service.DeliveredAt     = nowUtc;
                break;
            case "CANCELLED":
                if (service.CancelledAt     == null) service.CancelledAt     = nowUtc;
                break;
            // NOT_STARTED — no timestamp side effects.
        }

        service.Status = newStatus;

        // Recompute parent rollup using the other live services on the visit
        // (plus the one we just edited, since its in-memory edit hasn't been
        // saved yet but EF's ChangeTracker has it). Soft-deleted services
        // are excluded from the rollup so a cancelled scan doesn't keep the
        // visit "pending" forever.
        var siblings = await _context.AppointmentServices
            .Where(s => s.AppointmentId == appointment.AppointmentId
                     && s.DeletedAt == null
                     && s.Id != service.Id)
            .Select(s => new
            {
                s.Status,
                s.ScanStartedAt,
                s.ScanCompletedAt,
                s.DeliveredAt
            })
            .ToListAsync(cancellationToken);

        var allLive = siblings
            .Append(new
            {
                service.Status,
                service.ScanStartedAt,
                service.ScanCompletedAt,
                service.DeliveredAt
            })
            .ToList();

        // ScanStartedAt rollup. Set on the parent the moment ANY service has
        // started — drives the on-premises clock and the worklist's
        // SCANNING badge.
        var earliestStart = allLive
            .Where(x => x.ScanStartedAt.HasValue)
            .Select(x => x.ScanStartedAt!.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Min();
        if (earliestStart != DateTime.MinValue)
        {
            if (appointment.ScanStartedAt == null || earliestStart < appointment.ScanStartedAt)
                appointment.ScanStartedAt = earliestStart;
        }

        var nonCancelled = allLive
            .Where(x => !string.Equals(x.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var everyScanned   = nonCancelled.Count > 0
                              && nonCancelled.All(x => x.ScanCompletedAt.HasValue);
        var everyDelivered = nonCancelled.Count > 0
                              && nonCancelled.All(x => x.DeliveredAt.HasValue);

        if (everyScanned)
        {
            var latestScan = nonCancelled
                .Where(x => x.ScanCompletedAt.HasValue)
                .Max(x => x.ScanCompletedAt!.Value);
            if (appointment.ScannedAt == null || latestScan > appointment.ScannedAt)
                appointment.ScannedAt = latestScan;
        }
        if (everyDelivered)
        {
            var latestDelivered = nonCancelled
                .Where(x => x.DeliveredAt.HasValue)
                .Max(x => x.DeliveredAt!.Value);
            if (appointment.DeliveredAt == null || latestDelivered > appointment.DeliveredAt)
                appointment.DeliveredAt = latestDelivered;
        }

        // Parent status rollup. We only flip UPWARD — a per-service
        // correction shouldn't bump the visit back to "scheduled" just
        // because one line was reset.
        var currentParent = (appointment.Status ?? string.Empty).ToLowerInvariant();
        bool anyStarted  = nonCancelled.Any(x => x.ScanStartedAt.HasValue
                                              || string.Equals(x.Status, "SCANNED",   StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Status, "REPORTED",  StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(x.Status, "DELIVERED", StringComparison.OrdinalIgnoreCase));

        if (everyDelivered)
        {
            appointment.Status = "delivered";
        }
        else if (everyScanned)
        {
            // Don't downgrade a reporting/reported visit because one
            // service was just re-scanned.
            if (currentParent is "scheduled" or "booked" or "confirmed" or "in_progress" or "")
                appointment.Status = "scanned";
        }
        else if (anyStarted)
        {
            if (currentParent is "scheduled" or "booked" or "confirmed" or "")
                appointment.Status = "in_progress";
        }

        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateAppointmentServiceStatusResult
        {
            Success = true,
            AppointmentServiceId       = service.Id,
            ServiceStatus              = service.Status,
            ServiceScanStartedAt       = service.ScanStartedAt,
            ServiceScanCompletedAt     = service.ScanCompletedAt,
            ServiceReportedAt          = service.ReportedAt,
            ServiceDeliveredAt         = service.DeliveredAt,
            ServiceCancelledAt         = service.CancelledAt,
            AppointmentStatus          = appointment.Status,
            AppointmentScanStartedAt   = appointment.ScanStartedAt,
            AppointmentScannedAt       = appointment.ScannedAt,
            AppointmentDeliveredAt     = appointment.DeliveredAt,
        };
    }
}
