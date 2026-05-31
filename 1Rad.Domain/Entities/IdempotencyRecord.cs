namespace _1Rad.Domain.Entities;

// Phase B2 Track 2 — server-side replay table. Each successful mutating
// request is stamped here, and a retry within the TTL window short-
// circuits with the stored response instead of executing the action
// twice. See IdempotencyMiddleware for the full flow.
public class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    // Nullable for AllowAnonymous endpoints — the key alone has to
    // disambiguate in that case.
    public Guid? UserId { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? ResponseContentType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
