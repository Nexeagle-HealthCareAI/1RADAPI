using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.UpdateAppointmentServiceNotes;

public class UpdateAppointmentServiceNotesResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    // Echo back the saved comment + updated row stamp so the client
    // can refresh its local cache without a full refetch.
    public Guid? AppointmentServiceId { get; set; }
    public string? ServiceNotes { get; set; }
    public DateTime? ServiceUpdatedAt { get; set; }
}

/// <summary>
/// Per-service notes (ops-facing). One AppointmentService line on a
/// visit can carry its own short narrative — "patient asked to come
/// back tomorrow", "needed pre-med, rescheduled scan" — separate from
/// the visit's DelayReason which is shared across all services.
///
/// Stored in <see cref="_1Rad.Domain.Entities.AppointmentService.TechnicianComments"/>.
/// Capped at 500 chars to keep the popover input + the tooltip
/// readable.
/// </summary>
public record UpdateAppointmentServiceNotesCommand(
    Guid AppointmentId,
    Guid AppointmentServiceId,
    string? Notes
) : IRequest<UpdateAppointmentServiceNotesResult>;

public class UpdateAppointmentServiceNotesCommandHandler
    : IRequestHandler<UpdateAppointmentServiceNotesCommand, UpdateAppointmentServiceNotesResult>
{
    private const int MaxLength = 500;

    private readonly IApplicationDbContext _context;

    public UpdateAppointmentServiceNotesCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<UpdateAppointmentServiceNotesResult> Handle(
        UpdateAppointmentServiceNotesCommand request,
        CancellationToken cancellationToken)
    {
        var service = await _context.AppointmentServices
            .FirstOrDefaultAsync(s =>
                s.Id == request.AppointmentServiceId &&
                s.AppointmentId == request.AppointmentId,
                cancellationToken);

        if (service == null)
        {
            return new UpdateAppointmentServiceNotesResult
            {
                Success = false,
                Message = "Service not found on this appointment.",
            };
        }

        // Normalise: trim, treat empty string as "clear the note" so
        // the operator can wipe a stale comment without us storing a
        // whitespace-only row that the UI would still treat as "has
        // notes" because of the 📝 marker.
        var trimmed = request.Notes?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            service.TechnicianComments = null;
        }
        else
        {
            // Hard cap so a runaway paste doesn't ship a 50KB blob to
            // every worklist poll. Truncates silently — the textarea
            // already limits this on the client; this is a server-
            // side belt-and-braces.
            service.TechnicianComments = trimmed.Length > MaxLength
                ? trimmed.Substring(0, MaxLength)
                : trimmed;
        }

        service.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new UpdateAppointmentServiceNotesResult
        {
            Success                = true,
            AppointmentServiceId   = service.Id,
            ServiceNotes           = service.TechnicianComments,
            ServiceUpdatedAt       = service.UpdatedAt,
        };
    }
}
