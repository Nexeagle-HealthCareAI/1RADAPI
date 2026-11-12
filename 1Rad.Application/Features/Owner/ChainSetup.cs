using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Owner;

/// <summary>A centre the user is an admin of (AdminDoctor or Admin), so it shows on their owner board.</summary>
public record OwnedCentre(Guid HospitalId, string Name, Guid? GroupId, string? GroupName, bool IsDefault, DateTime CreatedAt);

/// <summary>
/// Who may see a centre on the owner board: the user must be an admin there.
/// Always checked against the database, never only against the login token, so a
/// role taken away today stops the board at once.
/// </summary>
public static class OwnerAccess
{
    private static readonly string[] OwnerRoles = { RoleConstants.AdminDoctor, RoleConstants.AdminOperator };

    public static async Task<List<OwnedCentre>> CentresAsync(IApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) return new List<OwnedCentre>();
        var rows = await db.UserHospitalMappings.AsNoTracking().IgnoreQueryFilters()
            .Where(m => m.UserId == userId && m.Roles.Any(r => OwnerRoles.Contains(r.RoleName)) && m.Hospital.Status != "Deleted")
            .Select(m => new
            {
                m.HospitalId, m.Hospital.HospitalName, m.Hospital.GroupId,
                GroupName = m.Hospital.Group != null ? m.Hospital.Group.GroupName : null,
                m.IsDefault, m.Hospital.CreatedAt,
            })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.HospitalId).Select(g => g.First())
            .OrderByDescending(r => r.IsDefault).ThenBy(r => r.CreatedAt)
            .Select(r => new OwnedCentre(r.HospitalId, string.IsNullOrWhiteSpace(r.HospitalName) ? "Centre" : r.HospitalName!, r.GroupId, r.GroupName, r.IsDefault, r.CreatedAt))
            .ToList();
    }

    public static async Task<List<OwnedCentre>> RequireAsync(IApplicationDbContext db, Guid userId, IEnumerable<Guid> hospitalIds, CancellationToken ct)
    {
        var mine = await CentresAsync(db, userId, ct);
        var wanted = hospitalIds.Distinct().ToList();
        var missing = wanted.Where(id => mine.All(c => c.HospitalId != id)).ToList();
        if (missing.Count > 0) throw new ForbiddenException("You can only do this for centres where you are an owner (admin).");
        return mine.Where(c => wanted.Contains(c.HospitalId)).ToList();
    }
}

/// <summary>What a copy did at one centre.</summary>
public record ChainCopyResult(Guid HospitalId, bool BillingCopied, int PricesAdded, int PricesUpdated, int PricesOnlyHere);

/// <summary>
/// Copying one centre's setup to another, and keeping linked centres in step.
/// Billing = every rule in HospitalBillingSettings (discount limits, reason rule,
/// partner payout rule, GST, credit notes, TDS) and automatic billing. Prices = the price list, matched
/// by modality + test name. Tests that exist only at the target are never deleted:
/// they may be on old bills, and the owner board lists them so the owner decides.
/// Report templates belong to each centre, so a new price is copied without one
/// and an existing price keeps its own.
/// All writes go through IgnoreQueryFilters and name the centre explicitly.
/// </summary>
public static class ChainSetup
{
    public static async Task<bool> CopyBillingAsync(IApplicationDbContext db, Guid fromId, Guid toId, CancellationToken ct)
    {
        // Automatic billing (a bill made by itself when a visit is completed) lives on the
        // centre itself, not in HospitalBillingSettings; it is part of "billing rules" too.
        var autoBill = await db.Hospitals.AsNoTracking().Where(h => h.HospitalId == fromId).Select(h => (bool?)h.IsAutoBillingEnabled).FirstOrDefaultAsync(ct);
        var target = await db.Hospitals.FirstOrDefaultAsync(h => h.HospitalId == toId, ct);
        if (autoBill.HasValue && target != null) target.IsAutoBillingEnabled = autoBill.Value;

        var src = await db.HospitalBillingSettings.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.HospitalId == fromId, ct);
        var dst = await db.HospitalBillingSettings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.HospitalId == toId, ct);
        if (src == null)
        {
            // The main centre runs on the defaults: so should this one.
            if (dst == null) return false;
            db.HospitalBillingSettings.Remove(dst);
            return true;
        }
        if (dst == null)
        {
            dst = new HospitalBillingSettings { HospitalId = toId };
            db.HospitalBillingSettings.Add(dst);
        }
        dst.GstEnabled = src.GstEnabled;
        dst.CreditNotesEnabled = src.CreditNotesEnabled;
        dst.TdsEnabled = src.TdsEnabled;
        dst.DefaultTdsCategory = src.DefaultTdsCategory;
        dst.TdsCommissionRate = src.TdsCommissionRate;
        dst.TdsCommissionThreshold = src.TdsCommissionThreshold;
        dst.TdsProfessionalRate = src.TdsProfessionalRate;
        dst.TdsProfessionalThreshold = src.TdsProfessionalThreshold;
        dst.TdsNoPanRate = src.TdsNoPanRate;
        dst.FrontDeskDiscountLimitPercent = src.FrontDeskDiscountLimitPercent;
        dst.AccountantDiscountLimitPercent = src.AccountantDiscountLimitPercent;
        dst.DiscountReasonRequired = src.DiscountReasonRequired;
        dst.PartnerPayWhenBilled = src.PartnerPayWhenBilled;
        dst.UpdatedByUserId = src.UpdatedByUserId;
        dst.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    public static string Key(string? modality, string? name) =>
        $"{(modality ?? string.Empty).Trim().ToUpperInvariant()}|{(name ?? string.Empty).Trim().ToUpperInvariant()}";

    public static async Task<(int Added, int Updated, int OnlyHere)> CopyPricesAsync(IApplicationDbContext db, Guid fromId, Guid toId, CancellationToken ct)
    {
        var src = await db.ServiceCharges.IgnoreQueryFilters().AsNoTracking().Where(s => s.HospitalId == fromId).ToListAsync(ct);
        var dst = await db.ServiceCharges.IgnoreQueryFilters().Where(s => s.HospitalId == toId).ToListAsync(ct);
        var byKey = dst.GroupBy(d => Key(d.Modality, d.ServiceName)).ToDictionary(g => g.Key, g => g.First());
        int added = 0, updated = 0;
        foreach (var s in src)
        {
            if (byKey.TryGetValue(Key(s.Modality, s.ServiceName), out var d))
            {
                if (d.Amount != s.Amount || d.ReferralCutValue != s.ReferralCutValue || d.GstRate != s.GstRate || d.SacCode != s.SacCode)
                {
                    d.Amount = s.Amount; d.ReferralCutValue = s.ReferralCutValue; d.GstRate = s.GstRate; d.SacCode = s.SacCode;
                    updated++;
                }
            }
            else
            {
                db.ServiceCharges.Add(new ServiceCharge
                {
                    HospitalId = toId, Modality = s.Modality, ServiceName = s.ServiceName, Amount = s.Amount,
                    ReferralCutValue = s.ReferralCutValue, GstRate = s.GstRate, SacCode = s.SacCode, TemplateId = null,
                });
                added++;
            }
        }
        var srcKeys = src.Select(s => Key(s.Modality, s.ServiceName)).ToHashSet();
        var onlyHere = byKey.Keys.Count(k => !srcKeys.Contains(k));
        return (added, updated, onlyHere);
    }

    /// <summary>
    /// Copy (and optionally link) one centre's setup to others. Does not save; the caller does.
    /// A centre that itself follows another can't be the source (no chains of links),
    /// and a main centre can't be made to follow one of its own followers.
    /// </summary>
    public static async Task<List<ChainCopyResult>> CopyAsync(IApplicationDbContext db, Guid fromId, IReadOnlyCollection<Guid> toIds,
        bool billing, bool prices, bool keepLinked, Guid? byUserId, CancellationToken ct)
    {
        if (!billing && !prices) throw new ValidationException("Pick what to copy: billing rules, prices or both.");
        var targets = toIds.Where(id => id != fromId && id != Guid.Empty).Distinct().ToList();
        if (targets.Count == 0) throw new ValidationException("Pick at least one other centre.");

        var links = await db.HospitalSettingsLinks.IgnoreQueryFilters()
            .Where(l => l.HospitalId == fromId || targets.Contains(l.HospitalId) || targets.Contains(l.SourceHospitalId))
            .ToListAsync(ct);
        if (keepLinked && links.Any(l => l.HospitalId == fromId && (l.LinkBilling || l.LinkPrices)))
            throw new ValidationException("This centre follows another centre. Copy from the main centre instead.");
        if (keepLinked && links.Any(l => targets.Contains(l.SourceHospitalId)))
            throw new ValidationException("A centre you picked is itself a main centre for others. Unlink those first.");

        var results = new List<ChainCopyResult>();
        foreach (var to in targets)
        {
            var b = billing && await CopyBillingAsync(db, fromId, to, ct);
            var p = prices ? await CopyPricesAsync(db, fromId, to, ct) : (0, 0, 0);
            results.Add(new ChainCopyResult(to, b, p.Item1, p.Item2, p.Item3));

            if (!keepLinked) continue;
            var link = links.FirstOrDefault(l => l.HospitalId == to);
            if (link == null)
            {
                link = new HospitalSettingsLink { HospitalId = to };
                db.HospitalSettingsLinks.Add(link);
                links.Add(link);
            }
            else if (link.SourceHospitalId != fromId)
            {
                // Moving to a new main centre: only the parts copied now follow it.
                link.LinkBilling = false;
                link.LinkPrices = false;
            }
            link.SourceHospitalId = fromId;
            link.LinkBilling |= billing;
            link.LinkPrices |= prices;
            link.LinkedByUserId = byUserId;
            link.LinkedAt = DateTime.UtcNow;
        }
        return results;
    }

    /// <summary>After the main centre saves its billing rules: the same rules at every centre that follows them. Saves.</summary>
    public static async Task PushBillingAsync(IApplicationDbContext db, Guid sourceId, CancellationToken ct)
    {
        var followers = await db.HospitalSettingsLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SourceHospitalId == sourceId && l.LinkBilling).Select(l => l.HospitalId).ToListAsync(ct);
        if (followers.Count == 0) return;
        foreach (var f in followers) await CopyBillingAsync(db, sourceId, f, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// After the main centre saves one price: the same price at every centre that
    /// follows its price list. <paramref name="oldKey"/> finds the test there if it
    /// was just renamed. Saves.
    /// </summary>
    public static async Task PushPriceAsync(IApplicationDbContext db, ServiceCharge saved, string? oldKey, CancellationToken ct)
    {
        var followers = await db.HospitalSettingsLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SourceHospitalId == saved.HospitalId && l.LinkPrices).Select(l => l.HospitalId).ToListAsync(ct);
        if (followers.Count == 0) return;
        var newKey = Key(saved.Modality, saved.ServiceName);
        var rows = await db.ServiceCharges.IgnoreQueryFilters().Where(s => followers.Contains(s.HospitalId)).ToListAsync(ct);
        foreach (var f in followers)
        {
            var mine = rows.Where(r => r.HospitalId == f).ToList();
            var d = mine.FirstOrDefault(r => Key(r.Modality, r.ServiceName) == (oldKey ?? newKey))
                    ?? mine.FirstOrDefault(r => Key(r.Modality, r.ServiceName) == newKey);
            if (d == null)
            {
                db.ServiceCharges.Add(new ServiceCharge
                {
                    HospitalId = f, Modality = saved.Modality, ServiceName = saved.ServiceName, Amount = saved.Amount,
                    ReferralCutValue = saved.ReferralCutValue, GstRate = saved.GstRate, SacCode = saved.SacCode,
                });
                continue;
            }
            // A rename onto a name the follower already has would make a duplicate: leave that one alone.
            if (oldKey != null && oldKey != newKey && mine.Any(r => r != d && Key(r.Modality, r.ServiceName) == newKey)) continue;
            d.Modality = saved.Modality; d.ServiceName = saved.ServiceName; d.Amount = saved.Amount;
            d.ReferralCutValue = saved.ReferralCutValue; d.GstRate = saved.GstRate; d.SacCode = saved.SacCode;
        }
        await db.SaveChangesAsync(ct);
    }
}
