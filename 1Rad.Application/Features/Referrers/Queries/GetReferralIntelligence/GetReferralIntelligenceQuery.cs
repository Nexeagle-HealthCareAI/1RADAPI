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

/// <param name="SummaryOnly">One row per source with every total but NO visit rows (Patients is empty). The screen loads this, then fetches a source's visits only when it is opened.</param>
/// <param name="SourceKey">Restrict to one source (the SourceKey a summary row carries: a partner's id, "self", "unattributed" or "name:XYZ").</param>
/// <param name="Skip">With Take: page through that source's visits (newest first). Totals still cover every visit of the source.</param>
public record GetReferralIntelligenceQuery(
    DateTime? StartDate = null,
    DateTime? EndDate = null,
    Guid? ReferrerId = null,
    bool SummaryOnly = false,
    string? SourceKey = null,
    int Skip = 0,
    int? Take = null
) : IRequest<List<ReferrerIntelligenceDto>>;

/// <summary>
/// Per-source referral rollup behind the Referrals page and the admin board.
///
/// Money and visits come from two deliberately separate places, and this
/// handler keeps them from contradicting each other or the Referral Hub:
///   • VISITS / revenue - the appointments in range, each attributed by
///     <see cref="ReferralAttribution"/> (the visit's own ReferredBy first; the patient's
///     "current" referrer only as a fallback). Only ATTENDED visits (the patient arrived)
///     count as visits - booked-not-arrived and no-show visits are reported separately.
///   • COMMISSION money - the live commission rows themselves, grouped by the
///     (merge-resolved) referrer that owns each row and bucketed by ServiceDate
///     in IST - exactly what /referrers/commissions serves the Hub. A visit's
///     commission is attributed only to rows owned by the source it is shown
///     under, so a re-assigned visit no longer bleeds the old partner's PAID row
///     into the new partner's "paid" figure.
///
/// Every visit lands somewhere: a partner, Self / walk-in, an UNLINKED source (a typed name
/// with no partner record) or UNATTRIBUTED (no referrer recorded) - so the totals reconcile
/// with the visit count and missing data is visible instead of silently dropped.
/// </summary>
public class GetReferralIntelligenceQueryHandler : IRequestHandler<GetReferralIntelligenceQuery, List<ReferrerIntelligenceDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly IUserContext _userContext;

    public GetReferralIntelligenceQueryHandler(IApplicationDbContext context, IUserContext userContext)
    {
        _context = context;
        _userContext = userContext;
    }

    private static string KindName(SourceKind kind) => kind switch
    {
        SourceKind.Partner => "PARTNER",
        SourceKind.Self => "SELF",
        SourceKind.Unlinked => "UNLINKED",
        _ => "UNATTRIBUTED",
    };

    public async Task<List<ReferrerIntelligenceDto>> Handle(GetReferralIntelligenceQuery request, CancellationToken cancellationToken)
    {
        var hospitalId = _userContext.HospitalId;

        // ── Referrer registry ───────────────────────────────────────────────────
        var attribution = new ReferralAttribution(
            (await _context.Referrers.AsNoTracking()
                .Where(r => r.HospitalId == hospitalId)
                .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt })
                .ToListAsync(cancellationToken))
            .Select(r => new ReferralAttribution.Entry(r.ReferrerId, r.MergedIntoId, r.Name, r.Contact, r.Address, r.DeletedAt)));

        // ── Optional single-partner filter, expanded to every merged alias ──────
        string? filterKey = null;
        List<Guid>? aliasIds = null;
        List<string>? aliasNames = null;
        // A partner's SourceKey is its (root) id, so it filters exactly like ReferrerId.
        var filterReferrerId = request.ReferrerId ?? (Guid.TryParse(request.SourceKey, out var keyAsId) ? keyAsId : (Guid?)null);
        if (filterReferrerId.HasValue)
        {
            filterKey = attribution.KeyForReferrer(filterReferrerId.Value);
            aliasIds = attribution.AliasIdsOf(filterReferrerId.Value);
            aliasNames = attribution.AliasNamesOf(filterReferrerId.Value);
        }
        if (!string.IsNullOrWhiteSpace(request.SourceKey)) filterKey = request.SourceKey.Trim();

        // Bare "YYYY-MM-DD" range -> IST day boundaries (see IstDateRange).
        DateTime? fromUtc = request.StartDate.HasValue ? IstDateRange.ToUtcStart(request.StartDate.Value) : null;
        DateTime? toUtc = request.EndDate.HasValue ? IstDateRange.ToUtcEndInclusive(request.EndDate.Value) : null;

        // ── Every non-cancelled visit in range (attendance is decided in memory) ─
        var appointmentsQuery = _context.Appointments
            .AsNoTracking()
            .Where(a => a.HospitalId == hospitalId)
            .Where(a => a.Status != "CANCELLED");

        if (fromUtc.HasValue) appointmentsQuery = appointmentsQuery.Where(a => a.DateTime >= fromUtc.Value);
        if (toUtc.HasValue) appointmentsQuery = appointmentsQuery.Where(a => a.DateTime <= toUtc.Value);
        if (aliasIds != null && aliasNames != null)
        {
            // A superset pre-filter (the exact partner is decided by attribution below): the
            // visit's own ReferrerId, its name (rows with no id), or the patient's link.
            appointmentsQuery = appointmentsQuery.Where(a =>
                (a.ReferrerId != null && aliasIds.Contains(a.ReferrerId.Value))
                || (a.Patient.ReferrerId != null && aliasIds.Contains(a.Patient.ReferrerId.Value))
                || (a.ReferredBy != null && aliasNames.Contains(a.ReferredBy)));
        }

        var rawMissions = await appointmentsQuery
            .Select(a => new
            {
                a.AppointmentId,
                a.DateTime,
                a.Status,
                a.ArrivedAt,
                a.Modality,
                a.Service,
                a.ReferredBy,
                AppointmentReferrerId = a.ReferrerId,
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

        var nowUtc = DateTime.UtcNow;
        var classified = rawMissions
            .Select(m => new
            {
                Mission = m,
                Source = attribution.Attribute(m.ReferredBy, m.PatientReferrerId, m.AppointmentReferrerId),
                Class = AppointmentAttendance.Classify(m.Status, m.ArrivedAt, m.DateTime, nowUtc),
            })
            .Where(x => filterKey == null || string.Equals(x.Source.Key, filterKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var missions = classified.Where(x => x.Class == AppointmentAttendance.Attended).ToList();
        var notAttendedByKey = classified
            .Where(x => x.Class != AppointmentAttendance.Attended)
            .GroupBy(x => x.Source.Key)
            .ToDictionary(g => g.Key, g => (Upcoming: g.Count(x => x.Class == AppointmentAttendance.Upcoming), NoShow: g.Count(x => x.Class == AppointmentAttendance.NoShow)));

        // ── Batched lookups for the attended visits ─────────────────────────────
        var apptIds = missions.Select(x => x.Mission.AppointmentId).ToList();
        var patientIds = missions.Select(x => x.Mission.PatientId).Distinct().ToList();

        // Which attended visits get a full row: none for a summary, one page (newest first) when
        // paging a source, otherwise all. TOTALS always cover every attended visit; only the rows
        // (and the per-visit commission lookup behind them) are limited.
        var pageByKey = missions
            .GroupBy(x => x.Source.Key)
            .ToDictionary(g => g.Key, g =>
            {
                if (request.SummaryOnly) return g.Take(0).ToList();
                var ordered = g.OrderByDescending(x => x.Mission.DateTime)
                    .ThenBy(x => x.Mission.AppointmentId)
                    .Skip(Math.Max(0, request.Skip));
                return (request.Take.HasValue ? ordered.Take(Math.Max(0, request.Take.Value)) : ordered).ToList();
            });
        var rowApptIds = pageByKey.Values.SelectMany(v => v).Select(x => x.Mission.AppointmentId).ToList();

        var invoicesByAppt = apptIds.Count == 0
            ? new Dictionary<Guid, (decimal Total, decimal Discount, decimal Paid)>()
            : (await _context.Invoices.AsNoTracking()
                    .Where(i => i.AppointmentId != null && apptIds.Contains(i.AppointmentId.Value)
                                && i.DeletedAt == null && i.Status != "CANCELLED")
                    .OrderBy(i => i.CreatedAt)
                    .Select(i => new { AppointmentId = i.AppointmentId!.Value, i.TotalAmount, i.DiscountAmount, i.PaidAmount })
                    .ToListAsync(cancellationToken))
                .GroupBy(i => i.AppointmentId)
                .ToDictionary(g => g.Key, g => (Total: g.First().TotalAmount, Discount: g.First().DiscountAmount, Paid: g.First().PaidAmount));

        var serviceLinesByAppt = apptIds.Count == 0
            ? new Dictionary<Guid, List<RawServiceLine>>()
            : (await _context.AppointmentServices.AsNoTracking()
                    .Where(s => apptIds.Contains(s.AppointmentId) && s.DeletedAt == null)
                    .OrderBy(s => s.UpdatedAt)
                    .Select(s => new RawServiceLine(s.AppointmentId, s.Id, s.ServiceName ?? string.Empty, s.Modality ?? "UNKNOWN"))
                    .ToListAsync(cancellationToken))
                .GroupBy(s => s.AppointmentId)
                .ToDictionary(g => g.Key, g => g.ToList());

        // The patient's FIRST attended visit at the centre, from any source and any date - so a
        // repeat visit is recognised as repeat even when the first one fell before this range.
        var firstVisitByPatient = patientIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : (await _context.Appointments.AsNoTracking()
                    .Where(a => a.HospitalId == hospitalId && patientIds.Contains(a.PatientId)
                                && a.Status != "CANCELLED"
                                && (a.ArrivedAt != null || AppointmentAttendance.AttendedStatuses.Contains(a.Status!)))
                    .Select(a => new { a.PatientId, a.AppointmentId, a.DateTime })
                    .ToListAsync(cancellationToken))
                .GroupBy(a => a.PatientId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.DateTime).ThenBy(x => x.AppointmentId).First().AppointmentId);

        // Live commission rows tied to these visits (any date - a visit's own rows are what its
        // per-row figures show), tagged with the source that owns them.
        var visitCommissions = rowApptIds.Count == 0
            ? new List<CommissionRow>()
            : (await _context.ReferralCommissions.AsNoTracking()
                    .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                                && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                                && c.AppointmentId != null && rowApptIds.Contains(c.AppointmentId.Value))
                    .Select(c => new { c.AppointmentId, c.ReferrerId, c.AppointmentServiceId, c.Modality, c.CommissionAmount, c.Status })
                    .ToListAsync(cancellationToken))
                .Select(c => new CommissionRow(c.AppointmentId, attribution.KeyForReferrer(c.ReferrerId), c.AppointmentServiceId, c.Modality, c.CommissionAmount, c.Status))
                .ToList();
        var visitCommissionsByAppt = visitCommissions
            .Where(c => c.AppointmentId.HasValue)
            .GroupBy(c => c.AppointmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Partner-level money: every live commission in the range, by owner. This is the same set
        // /referrers/commissions serves the Referral Hub (ServiceDate, IST day boundaries, cancelled
        // rows out, clawback deficits in).
        var moneyQuery = _context.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED");
        if (fromUtc.HasValue) moneyQuery = moneyQuery.Where(c => c.ServiceDate >= fromUtc.Value);
        if (toUtc.HasValue) moneyQuery = moneyQuery.Where(c => c.ServiceDate <= toUtc.Value);
        if (aliasIds != null) moneyQuery = moneyQuery.Where(c => aliasIds.Contains(c.ReferrerId));

        var moneyByGroup = (await moneyQuery
                .Select(c => new { c.ReferrerId, c.CommissionAmount, c.Status })
                .ToListAsync(cancellationToken))
            .Select(c => new { Key = attribution.KeyForReferrer(c.ReferrerId), c.CommissionAmount, c.Status })
            .Where(c => c.Key != null && c.Key != ReferralAttribution.SelfKey)
            .GroupBy(c => c.Key!)
            .ToDictionary(
                g => g.Key,
                g => (Total: g.Sum(x => x.CommissionAmount),
                      Paid: g.Where(x => CommissionStatus.IsPaid(x.Status)).Sum(x => x.CommissionAmount)));

        // ── Assemble per-source nodes ───────────────────────────────────────────
        var sourceByKey = new Dictionary<string, SourceRef>();
        foreach (var x in classified) sourceByKey.TryAdd(x.Source.Key, x.Source);
        foreach (var key in moneyByGroup.Keys)
            if (!sourceByKey.ContainsKey(key) && (filterKey == null || key == filterKey) && Guid.TryParse(key, out var rid))
                sourceByKey[key] = new SourceRef(key, SourceKind.Partner, attribution.ById.TryGetValue(rid, out var e) && !string.IsNullOrWhiteSpace(e.Name) ? e.Name! : "Unknown", rid);

        var attendedByKey = missions.GroupBy(x => x.Source.Key).ToDictionary(g => g.Key, g => g.ToList());

        var result = sourceByKey.Values.Select(src =>
        {
            attendedByKey.TryGetValue(src.Key, out var attended);
            attended ??= new();
            pageByKey.TryGetValue(src.Key, out var pageRows);
            pageRows ??= new();
            notAttendedByKey.TryGetValue(src.Key, out var na);
            var root = src.Kind == SourceKind.Partner ? attribution.RootEntry(src.RootId) : null;

            // ── Totals: every attended visit of this source, whatever page the rows cover ──
            var billed = 0m; var discount = 0m; var collected = 0m; var newPatients = 0;
            var modalities = new Dictionary<string, int>();
            foreach (var v in attended)
            {
                var m = v.Mission;
                if (invoicesByAppt.TryGetValue(m.AppointmentId, out var invoice))
                {
                    billed += invoice.Total; discount += invoice.Discount; collected += invoice.Paid;
                }
                if (firstVisitByPatient.TryGetValue(m.PatientId, out var firstId) && firstId == m.AppointmentId) newPatients++;

                // One count per service line (a CT + USG visit counts once in each), or the visit's
                // own modality when it has no lines - the same rule the screen used client-side.
                if (serviceLinesByAppt.TryGetValue(m.AppointmentId, out var lines) && lines.Count > 0)
                    foreach (var line in lines)
                    {
                        var mod = string.IsNullOrWhiteSpace(line.Modality) ? "OTHER" : line.Modality.ToUpperInvariant();
                        modalities[mod] = modalities.GetValueOrDefault(mod) + 1;
                    }
                else
                {
                    var mod = string.IsNullOrWhiteSpace(m.Modality) ? "OTHER" : m.Modality;
                    modalities[mod] = modalities.GetValueOrDefault(mod) + 1;
                }
            }

            var rows = pageRows.Select(x =>
            {
                var m = x.Mission;
                // Only the rows this source owns on this visit.
                var mine = visitCommissionsByAppt.TryGetValue(m.AppointmentId, out var all)
                    ? all.Where(c => c.GroupKey == src.Key).ToList()
                    : new List<CommissionRow>();

                var lines = serviceLinesByAppt.TryGetValue(m.AppointmentId, out var raw)
                    ? raw.Select(s =>
                    {
                        // Prefer a row tied to this exact service line (one commission per
                        // service); fall back to the legacy per-modality row.
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
                // "None" when the visit carries no commission at all (Self visit, or no cut
                // configured) - it used to read "Paid" because nothing was unpaid.
                var status = !mine.Any(c => c.Amount != 0m) ? "None"
                    : mine.Any(c => c.Amount != 0m && !CommissionStatus.IsPaid(c.Status)) ? "Unpaid"
                    : "Paid";

                invoicesByAppt.TryGetValue(m.AppointmentId, out var inv);
                var ist = IstDateRange.ToIst(m.DateTime);
                var isFirst = firstVisitByPatient.TryGetValue(m.PatientId, out var firstId) && firstId == m.AppointmentId;

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
                    ist.ToString("yyyy-MM-dd"),
                    m.Status,
                    m.AppointmentId,
                    totalForVisit,
                    status,
                    inv.Total,
                    src.DisplayName,
                    inv.Discount,
                    lines,
                    unpaidForVisit,
                    ist.ToString("yyyy-MM-ddTHH:mm"),
                    isFirst,
                    inv.Paid);
            })
            .ToList();

            moneyByGroup.TryGetValue(src.Key, out var money);

            return new ReferrerIntelligenceDto(
                src.Kind == SourceKind.Partner ? src.RootId : Guid.Empty,
                src.DisplayName,
                root?.Contact ?? string.Empty,
                root?.Address ?? string.Empty,
                attended.Count,
                rows,
                money.Total,
                money.Paid,
                money.Total - money.Paid,
                billed,
                discount,
                billed - money.Total,
                KindName(src.Kind),
                na.Upcoming,
                na.NoShow,
                attended.Select(x => x.Mission.PatientId).Distinct().Count(),
                newPatients,
                attended.Count - newPatients,
                collected,
                src.Key,
                modalities);
        })
        // A source only appears if it has something to show.
        // (A partner whose commission NETS to zero - e.g. 500 paid, 500 reversed - still has history.)
        .Where(n => n.TotalPatients > 0 || n.BookedPending > 0 || n.NoShows > 0
                    || n.TotalCommission != 0 || n.PaidCommission != 0 || n.UnpaidCommission != 0)
        .OrderByDescending(r => r.TotalPatients)
        .ThenBy(r => r.Name)
        .ToList();

        return result;
    }

    // Lightweight projection for the batched service-line lookup.
    private sealed record RawServiceLine(Guid AppointmentId, Guid Id, string ServiceName, string Modality);

    // A commission row tagged with the merge-resolved source that owns it.
    private sealed record CommissionRow(
        Guid? AppointmentId,
        string? GroupKey,
        Guid? AppointmentServiceId,
        string? Modality,
        decimal Amount,
        string? Status);
}
