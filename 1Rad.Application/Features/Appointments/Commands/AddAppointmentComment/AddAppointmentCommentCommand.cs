using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Commands.AddAppointmentComment;

// Result echoes the comment back to the client so it can render immediately
// without waiting for the timeline GET to re-fire.
public record AddAppointmentCommentResult(Guid AppointmentCommentId, DateTime CreatedAt);

public record AddAppointmentCommentCommand(Guid AppointmentId, string Body)
    : IRequest<AddAppointmentCommentResult?>;

public class AddAppointmentCommentCommandHandler
    : IRequestHandler<AddAppointmentCommentCommand, AddAppointmentCommentResult?>
{
    private readonly IApplicationDbContext _context;

    public AddAppointmentCommentCommandHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<AddAppointmentCommentResult?> Handle(
        AddAppointmentCommentCommand request,
        CancellationToken cancellationToken)
    {
        var body = (request.Body ?? string.Empty).Trim();
        if (body.Length == 0) return null;

        var appointment = await _context.Appointments
            .FirstOrDefaultAsync(a => a.AppointmentId == request.AppointmentId, cancellationToken);

        if (appointment == null) return null;

        var nowUtc = DateTime.UtcNow;
        var authorId = _context.UserContext.UserId != Guid.Empty ? _context.UserContext.UserId : (Guid?)null;

        // Resolve the author's display name once and denormalise it onto the
        // appointment so the worklist row can show "by {name}" without
        // joining Users on every read. Falls back to "System" for the
        // unauthenticated / background-job case (shouldn't happen from the
        // UI, but is correct for backfills and admin operations).
        string? authorName = null;
        if (authorId.HasValue)
        {
            authorName = await _context.Users
                .Where(u => u.UserId == authorId.Value)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken);
        }
        authorName = string.IsNullOrWhiteSpace(authorName) ? "System" : authorName;

        var comment = new AppointmentComment
        {
            AppointmentCommentId = Guid.NewGuid(),
            AppointmentId = appointment.AppointmentId,
            HospitalId = appointment.HospitalId,
            Body = body.Length > 2000 ? body.Substring(0, 2000) : body,
            AuthorUserId = authorId,
            CreatedAt = nowUtc,
        };
        _context.AppointmentComments.Add(comment);

        // Mirror the latest comment onto Appointment so worklist rows can
        // render the body + byline inline without a join.
        appointment.DelayReason = comment.Body;
        appointment.LatestCommentAuthorName = authorName;
        appointment.LatestCommentAt = nowUtc;

        await _context.SaveChangesAsync(cancellationToken);

        return new AddAppointmentCommentResult(comment.AppointmentCommentId, comment.CreatedAt);
    }
}
