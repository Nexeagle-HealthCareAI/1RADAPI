using _1Rad.Application.Common;
using _1Rad.Application.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Referrers.Queries.GetReferrersOutstanding;

/// <summary>
/// "Who is owed what" - one row per partner (merged duplicates rolled into their primary),
/// worked out on the server from the OPEN commission rows only (review #27). The Referral
/// Hub used to download every commission ever recorded and group it in the browser.
///
/// Per partner:
///  • PayableNow       - unpaid cuts whose patient has paid the bill in full (or manual
///                       lines with no visit): what can be paid today;
///  • AwaitingPatient  - unpaid cuts still waiting for the patient to pay;
///  • Deficit          - money the partner owes back (clawbacks, over-concessions), as a
///                       positive number; a payout can recover it;
///  • PaidThisYear     - gross paid out this financial year (payout headers);
///  • LastPaidAt       - when the partner was last paid.
/// show: "open" (default - anything owed either way), "payable", "awaiting", "deficit", "all".
/// </summary>
public record GetReferrersOutstandingQuery(string? Search = null, string? Show = null, int Page = 1, int PageSize = 50)
    : IRequest<ReferrersOutstandingResult>;

public record PartnerBalanceDto(
    Guid ReferrerId,
    string Name,
    bool IsDoctor,
    string? Contact,
    string? SupportedByDoctor,
    string? Specialty,
    bool HasPan,
    decimal PayableNow,
    int PayableCount,
    decimal AwaitingPatient,
    int AwaitingCount,
    decimal Deficit,
    decimal PaidThisYear,
    DateTime? LastPaidAt,
    int AliasCount);

public record OutstandingTotals(decimal PayableNow, decimal AwaitingPatient, decimal Deficit, int PartnersToPay);

public record ReferrersOutstandingResult(List<PartnerBalanceDto> Items, int Total, int Page, int PageSize, OutstandingTotals Totals, string FinancialYear);

public class GetReferrersOutstandingQueryHandler : IRequestHandler<GetReferrersOutstandingQuery, ReferrersOutstandingResult>
{
    private readonly IApplicationDbContext _context;

    public GetReferrersOutstandingQueryHandler(IApplicationDbContext context) => _context = context;

    public async Task<ReferrersOutstandingResult> Handle(GetReferrersOutstandingQuery request, CancellationToken ct)
    {
        var hospitalId = _context.UserContext.HospitalId;
        if (hospitalId == Guid.Empty)
            throw new UnauthorizedAccessException("Hospital context required.");

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var show = (request.Show ?? "open").Trim().ToLowerInvariant();

        var partners = await _context.Referrers.AsNoTracking()
            .Where(r => r.HospitalId == hospitalId)
            .Select(r => new { r.ReferrerId, r.MergedIntoId, r.Name, r.IsDoctor, r.Contact, r.SupportedByDoctor, r.Specialty, r.Pan, r.DeletedAt })
            .ToListAsync(ct);
        var byId = partners.ToDictionary(p => p.ReferrerId);
        Guid Root(Guid id)
        {
            var seen = new HashSet<Guid>();
            while (byId.TryGetValue(id, out var p) && p.MergedIntoId.HasValue && seen.Add(id)) id = p.MergedIntoId.Value;
            return id;
        }

        // Open rows only: bounded by what is outstanding, not by history.
        var open = await _context.ReferralCommissions.AsNoTracking()
            .Where(c => c.HospitalId == hospitalId && c.DeletedAt == null
                        && c.Status != CommissionStatus.Paid
                        && c.Status != CommissionStatus.Cancelled && c.Status != "CANCELLED"
                        && c.CommissionAmount != 0m)
            .Select(c => new { c.Id, c.ReferrerId, c.AppointmentId, c.ReferenceNumber, c.CommissionAmount })
            .ToListAsync(ct);

        // The patient's bill for each visit-linked row, read once and looked up by key.
        var apptIds = open.Where(c => c.AppointmentId.HasValue && c.CommissionAmount > 0).Select(c => c.AppointmentId!.Value).Distinct().ToList();
        var bills = apptIds.Count == 0
            ? new List<(Guid? AppointmentId, string InvoiceId, decimal PaidAmount, decimal TotalAmount, string? Status)>()
            : (await _context.Invoices.AsNoTracking()
                    .Where(i => i.HospitalId == hospitalId && i.DeletedAt == null && i.AppointmentId != null && apptIds.Contains(i.AppointmentId.Value))
                    .Select(i => new { i.AppointmentId, i.InvoiceId, i.PaidAmount, i.TotalAmount, i.Status })
                    .ToListAsync(ct))
                .Select(i => (i.AppointmentId, i.InvoiceId, i.PaidAmount, i.TotalAmount, i.Status))
                .ToList();
        var billByAppt = bills.Where(b => b.AppointmentId.HasValue)
            .GroupBy(b => b.AppointmentId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.PaidAmount).First());

        bool PatientPaid(Guid? appointmentId)
        {
            if (!appointmentId.HasValue) return true; // a manual line has no patient bill to wait for
            if (!billByAppt.TryGetValue(appointmentId.Value, out var b)) return false;
            return PatientPaymentStatus.IsPayable(PatientPaymentStatus.Resolve(b.PaidAmount, b.TotalAmount, b.Status));
        }

        var acc = new Dictionary<Guid, (decimal Pay, int PayN, decimal Wait, int WaitN, decimal Def)>();
        foreach (var c in open)
        {
            var root = Root(c.ReferrerId);
            acc.TryGetValue(root, out var a);
            if (c.CommissionAmount < 0) a.Def += -c.CommissionAmount;
            else if (PatientPaid(c.AppointmentId)) { a.Pay += c.CommissionAmount; a.PayN++; }
            else { a.Wait += c.CommissionAmount; a.WaitN++; }
            acc[root] = a;
        }

        var fy = InvoiceNumbering.FinancialYearLabel(DateTime.UtcNow);
        // Payout headers are one per payout (few), so they are aggregated here rather than
        // with a filtered aggregate inside a GROUP BY.
        var payouts = await _context.ReferralPayouts.AsNoTracking()
            .Where(p => p.HospitalId == hospitalId)
            .Select(p => new { p.ReferrerId, p.FinancialYear, p.Gross, p.PaidAt })
            .ToListAsync(ct);
        var paidByRoot = new Dictionary<Guid, (decimal Year, DateTime? Last)>();
        foreach (var p in payouts)
        {
            var root = Root(p.ReferrerId);
            paidByRoot.TryGetValue(root, out var x);
            if (p.FinancialYear == fy) x.Year += p.Gross;
            if (x.Last == null || p.PaidAt > x.Last) x.Last = p.PaidAt;
            paidByRoot[root] = x;
        }

        var aliasCounts = partners.Where(p => p.MergedIntoId.HasValue).GroupBy(p => Root(p.ReferrerId)).ToDictionary(g => g.Key, g => g.Count());

        // Primary partners only. A deleted primary still shows while it has money open,
        // so nothing owed silently disappears from the list.
        var rows = partners
            .Where(p => !p.MergedIntoId.HasValue && !NameNormalizer.SameName(p.Name, "Self"))
            .Where(p => p.DeletedAt == null || acc.ContainsKey(p.ReferrerId))
            .Select(p =>
            {
                acc.TryGetValue(p.ReferrerId, out var a);
                paidByRoot.TryGetValue(p.ReferrerId, out var paid);
                return new PartnerBalanceDto(
                    p.ReferrerId, p.Name ?? "Unknown", p.IsDoctor, p.Contact, p.SupportedByDoctor, p.Specialty,
                    !string.IsNullOrWhiteSpace(p.Pan),
                    a.Pay, a.PayN, a.Wait, a.WaitN, a.Def,
                    paid.Year, paid.Last,
                    aliasCounts.TryGetValue(p.ReferrerId, out var n) ? n : 0);
            })
            .ToList();

        var term = (request.Search ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            var key = NameNormalizer.Normalize(term);
            var digits = new string(term.Where(char.IsDigit).ToArray());
            rows = rows.Where(r => (key.Length > 0 && NameNormalizer.Normalize(r.Name).Contains(key))
                                   || (digits.Length >= 4 && (r.Contact ?? string.Empty).Contains(digits))
                                   || (key.Length > 0 && NameNormalizer.Normalize(r.SupportedByDoctor).Contains(key)))
                .ToList();
        }

        var totals = new OutstandingTotals(
            rows.Sum(r => r.PayableNow), rows.Sum(r => r.AwaitingPatient), rows.Sum(r => r.Deficit),
            rows.Count(r => r.PayableNow > 0));

        rows = show switch
        {
            "payable" => rows.Where(r => r.PayableNow > 0).ToList(),
            "awaiting" => rows.Where(r => r.AwaitingPatient > 0).ToList(),
            "deficit" => rows.Where(r => r.Deficit > 0).ToList(),
            "all" => rows,
            _ => rows.Where(r => r.PayableNow > 0 || r.AwaitingPatient > 0 || r.Deficit > 0).ToList(),
        };

        var ordered = rows
            .OrderByDescending(r => r.PayableNow)
            .ThenByDescending(r => r.AwaitingPatient)
            .ThenByDescending(r => r.Deficit)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReferrersOutstandingResult(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            ordered.Count, page, pageSize, totals, fy);
    }
}
