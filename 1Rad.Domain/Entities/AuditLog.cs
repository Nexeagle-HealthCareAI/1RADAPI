using _1Rad.Domain.Common;

namespace _1Rad.Domain.Entities;

/// <summary>
/// One row per changed entity, written in the same transaction as the change
/// (gap analysis P3-8). Table: dbo.AuditLogs (DB script 97). Report sign-off keeps its
/// own hash-chained ReportAuditEvents; this covers everything else that matters.
/// </summary>
public class AuditLog : IHospitalContext
{
    public long Id { get; set; }
    public Guid HospitalId { get; set; }
    public Guid? UserId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;   // Added | Modified | Deleted
    public string? ChangesJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
