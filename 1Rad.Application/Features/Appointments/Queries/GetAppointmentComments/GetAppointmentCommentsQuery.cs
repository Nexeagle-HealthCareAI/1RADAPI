using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Appointments.Queries.GetAppointmentComments;

// Author name resolved via LEFT JOIN against Users so legacy / backfilled
// comments without an AuthorUserId still render — they just show "System"
// rather than a name.
public record AppointmentCommentDto(
    Guid AppointmentCommentId,
    Guid AppointmentId,
    string Body,
    Guid? AuthorUserId,
    string AuthorName,
    DateTime CreatedAt
);

public record GetAppointmentCommentsQuery(Guid AppointmentId) : IRequest<List<AppointmentCommentDto>>;

public class GetAppointmentCommentsQueryHandler
    : IRequestHandler<GetAppointmentCommentsQuery, List<AppointmentCommentDto>>
{
    private readonly IApplicationDbContext _context;

    public GetAppointmentCommentsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<AppointmentCommentDto>> Handle(
        GetAppointmentCommentsQuery request,
        CancellationToken cancellationToken)
    {
        if (_context.UserContext.HospitalId == Guid.Empty)
            return new List<AppointmentCommentDto>();

        var hospitalId = _context.UserContext.HospitalId;

        // Newest first so the timeline reads the way an inbox does.
        var items = await _context.AppointmentComments
            .AsNoTracking()
            .Where(c => c.AppointmentId == request.AppointmentId
                     && c.HospitalId == hospitalId)
            .GroupJoin(_context.Users,
                c => c.AuthorUserId,
                u => u.UserId,
                (c, users) => new { Comment = c, Users = users })
            .SelectMany(x => x.Users.DefaultIfEmpty(),
                (x, u) => new { x.Comment, User = u })
            .OrderByDescending(x => x.Comment.CreatedAt)
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
