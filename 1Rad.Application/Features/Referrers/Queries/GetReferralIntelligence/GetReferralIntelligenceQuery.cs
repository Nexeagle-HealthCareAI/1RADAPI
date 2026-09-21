using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;

public record GetReferralIntelligenceQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null
) : IRequest<List<ReferrerIntelligenceDto>>;

/// <summary>
/// Per-partner referral rollup behind the Referrals page and the admin board.
///
/// Money and visits come from two deliberately separate places, and this
/// handler keeps them from contradicting each other or the Referral Hub:
///   • VISITS / revenue — the appointments in range, each attributed to the
///     referrer named on THAT visit (Appointment.ReferredBy). The patient's
///     "current" referrer is only a fallback: ChangeReferrer re-points it, which
///     used to drag every historical visit of that patient onto the new partner.
///   • COMMISSION money — the live commission rows themselves, grouped by the
///     (merge-resolved) referrer that owns each row and bucketed by ServiceDate
///     in IST — exactly what /referrers/commissions serves the Hub. A visit's
///     commission is attributed only to rows owned by the partner it is shown
///     under, so a re-assigned visit (paid row + reversal on the old partner,
///     fresh row on the new one) no longer bleeds the old partner's PAID row into
///     the new partner's "paid" figure.
/// </summary>
public class GetReferralIntelligenceQueryHandler : IRequestHandler<GetReferralIntelligenceQuery, List<ReferrerIntelligenceDto>>
{
    private const string SelfKey = "self";

    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetReferralIntelligenceQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    public async Task<List<ReferrerIntelligenceDto>> Handle(GetReferralIntelligenceQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        // ── Referrer registry: merge map + name lookup ──────────────────────────
        var allReferrers = await _context.Referrers
            .AsNoTracking()
            .Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt })
            .ToListAsync(cancellationToken);

        var referrersDict = allReferrers.ToDictionary(r => r.ReferrerId);
        var mergeMap = allReferrers.ToDictionary(r => r.ReferrerId, r => r.MergedIntoId);

        Guid ResolveReferrer(Guid id)
        {
            var current = id;
            var visited = new HashSet<Guid>();
            while (mergeMap.TryGetValue(current, out var next) && next.HasValue)
            {
                if (!visited.Add(current)) break; // cycle protection
                current = next.Value;
            }
            return current;
        }

        // A visit records its referrer as a NAME (Appointment.ReferredBy). Prefer a
        // live partner over a tombstoned one of the same name.
        var nameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allReferrers.OrderBy(r => r.DeletedAt != null).ThenBy(r => r.ReferrerId))
        {
            var key = (r.Name ?? string.Empty).Trim();
            if (key.Length > 0) nameToId.TryAdd(key, r.ReferrerId);
        }

        // Group key for a referrer id: merge-resolved, with the Self/walk-in record
        // collapsed into the single Self bucket. Unknown ids have no key.
        string? GroupKeyForReferrer(Guid id)
        {
            if (id == Guid.Empty) return null;
            var root = ResolveReferrer(id);
            if (referrersDict.TryGetValue(root, out var rr) && NameNormalizer.SameName(rr.Name, "Self")) return SelfKey;
            return root.ToString();
        }

        // ── Optional single-partner filter, expanded to every merged alias ──────
        string? filterKey = null;
        List<Guid>? aliasIds = null;
        List<string>? aliasNames = null;
        if (request.ReferrerId.HasValue)
        {
            filterKey = GroupKeyForReferrer(request.ReferrerId.Value);
            var root = ResolveReferrer(request.ReferrerId.Value);
            aliasIds = allReferrers.Where(r => ResolveReferrer(r.ReferrerId) == root).Select(r => r.ReferrerId).ToList();
            aliasNames = allReferrers
                .Where(r => ResolveReferrer(r.ReferrerId) == root && !string.IsNullOrWhiteSpace(r.Name))
                .Select(r => r.Name!.Trim()).Distinct().ToList();
        }

        // Bare "YYYY-MM-DD" range → IST day boundaries (see IstDateRange).
        DateTime? fromUtc = request.StartDate.HasValue ? IstDateRange.ToUtcStart(request.StartDate.Value) : null;
        DateTime? toUtc = request.EndDate.HasValue ? IstDateRange.ToUtcEndInclusive(request.EndDate.Value) : null;

        // ── Visits in range ─────────────────────────────────────────────────────
        var appointmentsQuery = _context.Appointments
            .AsNoTracking()
            .Where(a => a.HospitalId == hospitalId)
            .Where(a => a.Patient.ReferrerId != null || (a.ReferredBy != null && a.ReferredBy != string.Empty))
            // Cancelled visits are not referral business.
            .Where(a => a.Status != "CANCELLED");

        if (fromUtc.HasValue) appointmentsQuery = appointmentsQuery.Where(a => a.DateTime >= fromUtc.Value);
        if (toUtc.HasValue) appointmentsQuery = appointmentsQuery.Where(a => a.DateTime <= toUtc.Value);
        if (aliasIds != null && aliasNames != null)
        {
            appointmentsQuery = appointmentsQuery.Where(a =>
                (a.Patient.ReferrerId != null && aliasIds.Contains(a.Patient.ReferrerId.Value))
                || (a.ReferredBy != null && aliasNames.Contains(a.ReferredBy)));
        }

        var rawMissions = await appointmentsQuery
            .Select(a => new
            {
                a.AppointmentId,
                a.DateTime,
                a.Status,
                a.Modality,
                a.Service,
                a.ReferredBy,
                PatientReferrerId = a.Patient.ReferrerId,
                a.Patient.PatientId,
                a.Patient.PatientIdentifier,
                a.Patient.FullName,
                a.Patient.Mobile,
                a.Patient.Address,
                a.Patient.Age,
                a.Patient.Gender,
                a.Patient.SourceOfInfo,
            })
            .ToListAsync(cancellationToken);

        // Attribute each visit to a partner. The visit's own ReferredBy wins; the
        // patient's referrer link is only used when the visit names nobody.
        var missions = rawMissions
            .Select(m =>
            {
                var name = (m.ReferredBy ?? string.Empty).Trim();
                string groupKey;
                string displayName = name;
                if (name.Length > 0 && NameNormalizer.SameName(name, "Self"))
                {
                    groupKey = SelfKey;
                }
                else if (name.Length > 0 && nameToId.TryGetValue(name, out var byName))
                {
                    groupKey = GroupKeyForReferrer(byName) ?? ("name:" + name.ToUpperInvariant());
                }
                else if (m.PatientReferrerId.HasValue && GroupKeyForReferrer(m.PatientReferrerId.Value) is { } byPatient)
                {
                    groupKey = byPatient;
                }
                else
                {
                    // A free-text referrer that has no partner record (yet).
                    groupKey = "name:" + (name.Length > 0 ? name.ToUpperInvariant() : "ANONYMOUS SOURCE");
                    if (name.Length == 0) displayName = "Anonymous Source";
                }
                return new { Mission = m, GroupKey = groupKey, FreeTextName = displayName };
            })
            .Where(x => filterKey == null || x.GroupKey == filterKey)
            .ToList();

        // ── Batched lookups for those visits ────────────────────────────────────
        var apptIds = missions.Select(x => x.Mission.AppointmentId).ToList();

        var invoicesByAppt = apptIds.Count == 0
            ? new Dictionary<Guid, (decimal Total, decimal Discount)>()
            : (await _context.Invoices.AsNoTracking()
                    .Where(i => i.AppointmentId != null && apptIds.Contains(i.AppointmentId.Value)
                                && i.DeletedAt == null && i.Status != "CANCELLED")
                    .OrderBy(i => i.CreatedAt)
                    .Select(i => new { AppointmentId = i.AppointmentId!.Value, i.TotalAmount, i.DiscountAmount })
                    .ToListAsync(cancellationToken))
                .GroupBy(i => i.AppointmentId)
                .ToDictionary(g => g.Key, g => (Total: g.First().TotalAmount, Discount: g.First().DiscountAmount));

        var serviceLinesByAppt = apptIds.Count == 0
            ? new Dictionary<Guid, List<RawServiceLine>>()
            : (await _context.AppointmentServices.AsNoTracking()
                    .Where(s => apptIds.Contains(s.AppointmentId) && s.DeletedAt == null)
                    .OrderBy(s => s.UpdatedAt)
                    .Select(s => new RawServiceLine(s.AppointmentId, s.Id, s.ServiceName ?? string.Empty, s.Modality ?? "UNKNOWN"))
                    .ToListAsync(cancellationToken))
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => g.ToList());

        // Live commission rows tied to these visits (any date — a visit's own rows
        // are what its per-row figures show), tagged with the partner that owns them.
        var visitCommissions = apptIds.Count == 0
            ? new List<CommissionRow>()
            : (await _context.ReferralCommissions.AsNoTracking()
                    .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                                && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                                && c.AppointmentId != null && apptIds.Contains(c.AppointmentId.Value))
                    .Select(c => new { c.AppointmentId, c.ReferrerId, c.AppointmentServiceId, c.Modality, c.CommissionAmount, c.Status })
                    .ToListAsync(cancellationToken))
                .Select(c => new CommissionRow(c.AppointmentId, GroupKeyForReferrer(c.ReferrerId), c.AppointmentServiceId, c.Modality, c.CommissionAmount, c.Status))
                .ToList();
        var visitCommissionsByAppt = visitCommissions
            .Where(c => c.AppointmentId.HasValue)
            .GroupBy(c => c.AppointmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Partner-level money: every live commission in the range, by owner. This is
        // the same set /referrers/commissions serves the Referral Hub (ServiceDate,
        // IST day boundaries, cancelled rows out, clawback deficits in).
        var moneyQuery = _context.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED");
        if (fromUtc.HasValue) moneyQuery = moneyQuery.Where(c => c.ServiceDate >= fromUtc.Value);
        if (toUtc.HasValue) moneyQuery = moneyQuery.Where(c => c.ServiceDate <= toUtc.Value);
        if (aliasIds != null) moneyQuery = moneyQuery.Where(c => aliasIds.Contains(c.ReferrerId));

        var moneyByGroup = (await moneyQuery
                .Select(c => new { c.ReferrerId, c.CommissionAmount, c.Status })
                .ToListAsync(cancellationToken))
            .Select(c => new { Key = GroupKeyForReferrer(c.ReferrerId), c.CommissionAmount, c.Status })
            .Where(c => c.Key != null && c.Key != SelfKey)
            .GroupBy(c => c.Key!)
            .ToDictionary(
                g => g.Key,
                g => (Total: g.Sum(x => x.CommissionAmount),
                      Paid: g.Where(x => CommissionStatus.IsPaid(x.Status)).Sum(x => x.CommissionAmount)));

        // ── Assemble per-partner nodes ──────────────────────────────────────────
        var groups = missions.GroupBy(x => x.GroupKey).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var key in moneyByGroup.Keys)
            if (!groups.ContainsKey(key) && (filterKey == null || key == filterKey))
                groups[key] = new();   // partner with money in range but no visit in range

        var result = groups.Select(kv =>
        {
            var key = kv.Key;
            var isSelfGroup = key == SelfKey;
            Guid rootId = Guid.Empty;
            if (!isSelfGroup && !key.StartsWith("name:", StringComparison.Ordinal)) Guid.TryParse(key, out rootId);
            var root = rootId != Guid.Empty && referrersDict.TryGetValue(rootId, out var rr) ? rr : null;

            var firstFreeText = kv.Value.FirstOrDefault()?.FreeTextName;
            var rootName = root?.Name ?? (isSelfGroup ? "Self / Walk-in" : (string.IsNullOrWhiteSpace(firstFreeText) ? "Anonymous Source" : firstFreeText));
            var rootContact = root?.Contact ?? string.Empty;
            var rootAddress = root?.Address ?? string.Empty;

            var missionsList = kv.Value.Select(x =>
            {
                var m = x.Mission;
                // Only the rows this partner owns on this visit.
                var mine = visitCommissionsByAppt.TryGetValue(m.AppointmentId, out var all)
                    ? all.Where(c => c.GroupKey == key).ToList()
                    : new List<CommissionRow>();

                var lines = serviceLinesByAppt.TryGetValue(m.AppointmentId, out var raw)
                    ? raw.Select(s =>
                    {
                        // Prefer a row tied to this exact service line (one commission
                        // per service); fall back to the legacy per-modality row.
                        var matchedById = mine.Where(c => c.AppointmentServiceId == s.Id).Sum(c => c.Amount);
                        var attributed = matchedById != 0m
                            ? matchedById
                            : mine.Where(c => c.AppointmentServiceId == null
                                              && string.Equals(c.Modality, s.Modality, StringComparison.OrdinalIgnoreCase))
                                  .Sum(c => c.Amount);
                        return new ReferredServiceLineDto(s.Id, s.ServiceName, s.Modality, attributed);
                    }).ToList()
                    : new List<ReferredServiceLineDto>();

                var totalForVisit = mine.Sum(c => c.Amount);
                var unpaidForVisit = mine.Where(c => !CommissionStatus.IsPaid(c.Status)).Sum(c => c.Amount);
                // "None" when the visit carries no commission at all (Self visit, or no
                // cut configured) — it used to read "Paid" because nothing was unpaid.
                var status = !mine.Any(c => c.Amount != 0m) ? "None"
                    : mine.Any(c => c.Amount != 0m && !CommissionStatus.IsPaid(c.Status)) ? "Unpaid"
                    : "Paid";

                invoicesByAppt.TryGetValue(m.AppointmentId, out var inv);

                return new ReferredPatientDto(
                    m.PatientId,
                    m.PatientIdentifier,
                    m.FullName,
                    m.Mobile,
                    m.Address,
                    m.Age,
                    m.Gender,
                    m.Modality,
                    m.Service,
                    m.SourceOfInfo ?? "DIRECT",
                    m.DateTime.ToString("yyyy-MM-dd"),
                    m.Status,
                    m.AppointmentId,
                    totalForVisit,
                    status,
                    inv.Total,
                    rootName,
                    inv.Discount,
                    lines,
                    unpaidForVisit);
            })
            .OrderByDescending(p => p.RegistrationDate)
            .ToList();

            moneyByGroup.TryGetValue(key, out var money);
            var totalComm = money.Total;
            var paidComm = money.Paid;
            var totalRev = missionsList.Sum(p => p.TotalAmount);
            var totalDisc = missionsList.Sum(p => p.DiscountAmount);

            return new ReferrerIntelligenceDto(
                isSelfGroup ? Guid.Empty : rootId,
                rootName,
                rootContact,
                rootAddress,
                missionsList.Count,
                missionsList,
                totalComm,
                paidComm,
                totalComm - paidComm,
                totalRev,
                totalDisc,
                totalRev - totalComm);
        })
        .OrderByDescending(r => r.TotalPatients)
        .ThenBy(r => r.Name)
        .ToList();

        return result;
    }

    // Lightweight projection for the batched service-line lookup.
    private sealed record RawServiceLine(Guid AppointmentId, Guid Id, string ServiceName, string Modality);

    // A commission row tagged with the merge-resolved partner that owns it.
    private sealed record CommissionRow(
        Guid? AppointmentId,
        string? GroupKey,
        Guid? AppointmentServiceId,
        string? Modality,
        decimal Amount,
        string? Status);
}
