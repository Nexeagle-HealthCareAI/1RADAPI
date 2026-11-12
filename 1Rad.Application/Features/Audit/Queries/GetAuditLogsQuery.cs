using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Audit.Queries;

/// <summary>Who changed what at the current centre, newest first (gap analysis P3-8).</summary>
public record GetAuditLogsQuery(
    string? EntityType = null,
    string? EntityId = null,
    Guid? UserId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = 50) : IRequest<AuditLogPage>;

public record AuditLogDto(
    long Id,
    DateTime CreatedAt,
    Guid? UserId,
    string? UserName,
    string EntityType,
    string EntityId,
    string Action,
    string? ChangesJson);

public record AuditLogPage(List<AuditLogDto> Items, int Page, int PageSize, int TotalCount);

public class GetAuditLogsQueryHandler : IRequestHandler<GetAuditLogsQuery, AuditLogPage>
{
    private readonly IApplicationDbContext _db;

    public GetAuditLogsQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<AuditLogPage> Handle(GetAuditLogsQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);

        // AuditLog is IHospitalContext: the global filter scopes this to the caller's centre.
        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q.EntityType)) query = query.Where(a => a.EntityType == q.EntityType);
        if (!string.IsNullOrWhiteSpace(q.EntityId)) query = query.Where(a => a.EntityId == q.EntityId);
        if (q.UserId.HasValue) query = query.Where(a => a.UserId == q.UserId);
        if (q.FromUtc.HasValue) query = query.Where(a => a.CreatedAt >= q.FromUtc);
        if (q.ToUtc.HasValue) query = query.Where(a => a.CreatedAt < q.ToUtc);

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(a => a.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var userIds = rows.Where(r => r.UserId.HasValue).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FullName })
            .ToDictionaryAsync(u => u.UserId, u => u.FullName, ct);

        var items = rows.Select(r => new AuditLogDto(
            r.Id, r.CreatedAt, r.UserId,
            r.UserId.HasValue && names.TryGetValue(r.UserId.Value, out var n) ? n : null,
            r.EntityType, r.EntityId, r.Action, r.ChangesJson)).ToList();

        return new AuditLogPage(items, page, size, total);
    }
}
