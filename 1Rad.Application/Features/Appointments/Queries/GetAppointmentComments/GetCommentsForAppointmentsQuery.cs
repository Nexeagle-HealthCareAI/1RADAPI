using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointmentComments;

// Bulk variant of GetAppointmentCommentsQuery. Used by the operations Excel
// export so it can fetch every comment for the visible (filtered) set in one
// round-trip instead of N. The returned DTOs include AppointmentId so the
// client can group them per appointment before flattening into the sheet.
public record GetCommentsForAppointmentsQuery(IReadOnlyCollection<Guid> AppointmentIds)
    : IRequest<List<AppointmentCommentDto>>;

public class GetCommentsForAppointmentsQueryHandler
    : IRequestHandler<GetCommentsForAppointmentsQuery, List<AppointmentCommentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetCommentsForAppointmentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<AppointmentCommentDto>> Handle(
        GetCommentsForAppointmentsQuery request,
        CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            return new List<AppointmentCommentDto>();

        if (request.AppointmentIds == null || request.AppointmentIds.Count == 0)
            return new List<AppointmentCommentDto>();

        // Cap the input size — exports of >500 patients in one go are
        // exceptional and would generate enormous spreadsheets. Beyond that
        // the client should narrow the date filter first.
        var ids = request.AppointmentIds.Distinct().Take(500).ToList();
        var hospitalId = _context.UserContext.HospitalId;

        var items = await _context.AppointmentComments
            .AsNoTracking()
            .Where(c => ids.Contains(c.AppointmentId) && c.HospitalId == hospitalId)
            .GroupJoin(_context.Users,
                c => c.AuthorUserId,
                u => u.UserId,
                (c, users) => new { Comment = c, Users = users })
            .SelectMany(x => x.Users.DefaultIfEmpty(),
                (x, u) => new { x.Comment, User = u })
            // Oldest first so the client can render the trail chronologically
            // in the export ("Comment 1 → 2 → 3" reads left-to-right).
            .OrderBy(x => x.Comment.AppointmentId)
            .ThenBy(x => x.Comment.CreatedAt)
            .Select(x => new AppointmentCommentDto(
                x.Comment.AppointmentCommentId,
                x.Comment.AppointmentId,
                x.Comment.Body,
                x.Comment.AuthorUserId,
                x.User != null ? (x.User.FullName ?? "Unknown user") : "System",
                x.Comment.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        return items;
    }
}
