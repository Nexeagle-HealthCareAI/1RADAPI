namespace _1Rad.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByIp { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedByIp { get; set; }
    public string? ReplacedByToken { get; set; }

    // ── Session-management additions (migration 46) ─────────────────────────
    // Stable per-session identifier — embedded as the `sid` claim in the
    // access token. The session validation middleware reads this claim on
    // every request and looks it up against the revoked-set cache; this is
    // what lets a forced logout take effect mid-flight without rotating the
    // user's whole token family.
    public Guid? SessionId { get; set; }

    // DESKTOP / MOBILE / TABLET / UNKNOWN. Drives the "one-per-category, max
    // three concurrent" cap and the per-row icon on Settings → Active
    // Sessions.
    public string? DeviceCategory { get; set; }

    // Human-readable label for the sessions list, e.g. "Chrome 120 on Windows".
    public string? DeviceName { get; set; }

    // Raw UA + IP captured at login for audit / abuse triage.
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }

    // Updated by the session middleware (throttled to once per 30s per
    // session) so the idle-timeout sweeper and the "last activity" UI have a
    // real signal to read.
    public DateTime? LastSeenAt { get; set; }

    // Why this session ended. RevokedAt records *when*; this records *why*.
    // Values: USER / FORCED_BY_NEW_DEVICE / EXPIRED / IDLE_TIMEOUT / ADMIN / SUSPICIOUS / LEGACY.
    public string? LoggedOutReason { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt != null;
    public bool IsActive => !IsRevoked && !IsExpired;

    // Navigation property
    public User User { get; set; } = null!;
}
