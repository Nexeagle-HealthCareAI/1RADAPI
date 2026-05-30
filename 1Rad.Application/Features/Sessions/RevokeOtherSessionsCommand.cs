using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Sessions;

// "Sign out everywhere else" — keeps the caller's CURRENT session active,
// revokes every other active session for the same user.
public record RevokeOtherSessionsCommand(Guid CurrentSessionId) : IRequest<int>;

public class RevokeOtherSessionsCommandHandler : IRequestHandler<RevokeOtherSessionsCommand, int>
{
    private readonly IApplicationDbContext _context;
    private readonly IActiveSessionCache _cache;

    public RevokeOtherSessionsCommandHandler(IApplicationDbContext context, IActiveSessionCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<int> Handle(RevokeOtherSessionsCommand request, CancellationToken cancellationToken)
    {
        var userId = _context.UserContext.UserId;
        if (userId == Guid.Empty) return 0;

        var rows = await _context.RefreshTokens
            .Where(rt => rt.UserId == userId
                      && rt.SessionId != null
                      && rt.SessionId != request.CurrentSessionId
                      && rt.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return 0;

        var nowUtc = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.RevokedAt = nowUtc;
            row.LoggedOutReason = "USER";
            if (row.SessionId.HasValue) _cache.Revoke(row.SessionId.Value);
        }
        await _context.SaveChangesAsync(cancellationToken);

        // We only count distinct sessions, not rows — refresh-token rotation
        // can leave multiple rows per session id behind, and the caller
        // wants the human "X sessions ended" number.
        return rows.Select(r => r.SessionId!.Value).Distinct().Count();
    }
}
