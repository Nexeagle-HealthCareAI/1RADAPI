using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Sessions;

// Single-session revoke. Marks the row revoked, evicts the cache. Returns
// false if no matching active session belongs to the current user — this
// covers both "already revoked" and "session belongs to another user"
// without leaking which.
public record RevokeSessionCommand(Guid SessionId) : IRequest<bool>;

public class RevokeSessionCommandHandler : IRequestHandler<RevokeSessionCommand, bool>
{
    private readonly IApplicationDbContext _context;
    private readonly IActiveSessionCache _cache;

    public RevokeSessionCommandHandler(IApplicationDbContext context, IActiveSessionCache cache)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<bool> Handle(RevokeSessionCommand request, CancellationToken cancellationToken)
    {
        var userId = _context.UserContext.UserId;
        if (userId == Guid.Empty) return false;

        // Tenant-scope to the caller: a user can only kill their OWN sessions
        // via this command. Admin-driven revocation goes through a separate
        // command (not in Phase 1).
        var rows = await _context.RefreshTokens
            .Where(rt => rt.SessionId == request.SessionId
                      && rt.UserId == userId
                      && rt.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return false;

        var nowUtc = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.RevokedAt = nowUtc;
            row.LoggedOutReason = "USER";
        }
        await _context.SaveChangesAsync(cancellationToken);

        _cache.Revoke(request.SessionId);
        return true;
    }
}
