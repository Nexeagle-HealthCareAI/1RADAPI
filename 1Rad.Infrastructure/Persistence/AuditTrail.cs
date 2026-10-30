using System.Text.Json;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Common;
using _1Rad.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace _1Rad.Infrastructure.Persistence;

/// <summary>
/// Builds AuditLog rows from the change tracker just before SaveChanges (gap analysis
/// P3-8). The rows are added to the same SaveChanges, so a change and its audit record
/// commit or roll back together.
///
/// Audited: the records whose history matters for money, clinical workflow and access.
/// Never recorded: passwords, hashes, tokens; noise columns (UpdatedAt, RowVersion).
/// </summary>
internal static class AuditTrail
{
    private static readonly HashSet<Type> Audited = new()
    {
        typeof(Patient), typeof(Appointment), typeof(AppointmentService), typeof(ApprovalRequest),
        typeof(Invoice), typeof(InvoiceItem), typeof(InvoiceExtraCharge), typeof(Payment),
        typeof(CreditTransaction), typeof(Expense), typeof(ServiceCharge),
        typeof(Referrer), typeof(ReferralCommission),
        typeof(Hospital), typeof(User), typeof(UserHospitalMapping),
        typeof(CustomRole), typeof(CustomRolePermission),
        typeof(StaffMember), typeof(SalaryRevision), typeof(SalaryDisbursement),
        typeof(StaffLeaveRequest), typeof(HospitalLeavePolicy),
        typeof(HospitalSubscription), typeof(SubscriptionPaymentRequest),
        typeof(ImagingStudy), typeof(PrescriptionProtocol),
    };

    // Many-to-many join tables (role assignments) have no CLR type of their own.
    private static readonly HashSet<string> AuditedJoinTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserHospitalRoles", "UserHospitalCustomRoles",
    };

    private static readonly HashSet<string> NeverLogged = new(StringComparer.OrdinalIgnoreCase)
    {
        "Password", "PasswordHash", "CodeHash", "Token", "AccessToken", "RefreshToken",
        "ResponseBody", "RowVersion", "UpdatedAt",
    };

    private const int MaxStringLength = 500;

    public static List<AuditLog> Collect(ChangeTracker tracker, IUserContext user, DateTime nowUtc)
    {
        var rows = new List<AuditLog>();
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            var joinTable = entry.Metadata.HasSharedClrType ? entry.Metadata.GetTableName() : null;
            if (!Audited.Contains(entry.Metadata.ClrType) &&
                (joinTable == null || !AuditedJoinTables.Contains(joinTable))) continue;

            var changes = new Dictionary<string, object?>();
            string action = entry.State.ToString();

            foreach (var p in entry.Properties)
            {
                var name = p.Metadata.Name;
                if (NeverLogged.Contains(name)) continue;

                switch (entry.State)
                {
                    case EntityState.Added:
                        if (p.CurrentValue != null && !p.IsTemporary) changes[name] = Trim(p.CurrentValue);
                        break;
                    case EntityState.Modified:
                        if (!p.IsModified || Equals(p.OriginalValue, p.CurrentValue)) continue;
                        if (name == "DeletedAt" && p.OriginalValue == null && p.CurrentValue != null) action = "SoftDeleted";
                        changes[name] = new { from = Trim(p.OriginalValue), to = Trim(p.CurrentValue) };
                        break;
                    case EntityState.Deleted:
                        if (p.OriginalValue != null) changes[name] = Trim(p.OriginalValue);
                        break;
                }
            }
            if (entry.State == EntityState.Modified && changes.Count == 0) continue;

            var key = entry.Metadata.FindPrimaryKey();
            var entityId = key == null
                ? "?"
                : string.Join("|", key.Properties.Select(k =>
                {
                    var prop = entry.Property(k.Name);
                    return prop.IsTemporary ? "new" : prop.CurrentValue?.ToString() ?? "";
                }));

            var hospitalId = entry.Entity switch
            {
                IHospitalContext h when h.HospitalId != Guid.Empty => h.HospitalId,
                Hospital h => h.HospitalId,
                UserHospitalMapping m => m.HospitalId,
                _ => user.HospitalId,
            };

            rows.Add(new AuditLog
            {
                HospitalId = hospitalId,
                UserId = user.UserId == Guid.Empty ? null : user.UserId,
                EntityType = joinTable ?? entry.Metadata.ClrType.Name,
                EntityId = entityId.Length > 100 ? entityId[..100] : entityId,
                Action = action,
                ChangesJson = changes.Count == 0 ? null : JsonSerializer.Serialize(changes),
                CreatedAt = nowUtc,
            });
        }
        return rows;
    }

    private static object? Trim(object? v) =>
        v is string s && s.Length > MaxStringLength ? s[..MaxStringLength] + "…" : v;
}
