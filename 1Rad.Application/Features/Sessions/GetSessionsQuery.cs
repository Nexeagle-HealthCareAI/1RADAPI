using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Sessions;

public record SessionDto(
    Guid SessionId,
    string DeviceCategory,
    string? DeviceName,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime? LastSeenAt,
    DateTime ExpiresAt,
    bool IsCurrent
);

// CurrentSessionId is supplied from the controller (parsed off the JWT's
// `sid` claim) so the UI can render the "This device" badge without the
// client having to derive it.
public record GetSessionsQuery(Guid? CurrentSessionId) : IRequest<List<SessionDto>>;

public class GetSessionsQueryHandler : IRequestHandler<GetSessionsQuery, List<SessionDto>>
{
    private readonly IApplicationDbContext _context;

    public GetSessionsQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<List<SessionDto>> Handle(GetSessionsQuery request, CancellationToken cancellationToken)
    {
        if (_context.UserContext.UserId == Guid.Empty) return new List<SessionDto>();

        var nowUtc = DateTime.UtcNow;
        var rows = await _context.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.UserId == _context.UserContext.UserId
                      && rt.SessionId != null
                      && rt.RevokedAt == null
                      && rt.ExpiresAt > nowUtc)
            // The active-session cap is 3 total but we keep an over-fetch to
            // surface any stragglers, e.g., from races. The UI is tolerant.
            .OrderByDescending(rt => rt.LastSeenAt ?? rt.CreatedAt)
            .Take(10)
            .Select(rt => new SessionDto(
                rt.SessionId!.Value,
                rt.DeviceCategory ?? "UNKNOWN",
                rt.DeviceName,
                rt.IpAddress,
                rt.CreatedAt,
                rt.LastSeenAt,
                rt.ExpiresAt,
                request.CurrentSessionId.HasValue && rt.SessionId == request.CurrentSessionId.Value
            ))
            .ToListAsync(cancellationToken);

        return rows;
    }
}
