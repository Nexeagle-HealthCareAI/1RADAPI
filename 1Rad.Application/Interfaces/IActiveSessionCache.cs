namespace _1Rad.Application.Interfaces;

// Process-local cache that the session validation middleware checks on every
// request. Mirrors the canonical "active vs revoked" state of RefreshTokens
// without hitting the DB on the hot path.
//
// Strategy: cache the POSITIVE state ("sid X is active until exp") rather
// than the negative ("sid X is revoked"). The negative set grows forever;
// the positive set is bounded by the cap on concurrent sessions per user
// (3) × active users. Either approach works — positive is preferred because
// it lets the middleware short-circuit on a cold cache miss by going to DB
// rather than incorrectly trusting a JWT.
//
// When we go multi-instance we swap the implementation to a distributed
// cache (Redis) — the interface stays the same.
public interface IActiveSessionCache
{
    // Mark a session as active. Called on login and on any successful refresh.
    void MarkActive(Guid sessionId, DateTime expiresAt);

    // Mark a session as revoked. Called when the user logs out, when a new
    // device kicks an existing one, and by the idle-timeout sweeper.
    void Revoke(Guid sessionId);

    // True iff the session is in the active set AND not expired. The
    // middleware treats anything else (revoked / unknown / expired) as "not
    // active" and returns 401.
    bool IsActive(Guid sessionId);
}
