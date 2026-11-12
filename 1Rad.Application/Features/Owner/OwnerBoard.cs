using _1Rad.Application.Features.Appointments.Queries.GetOverdueAppointments;
using _1Rad.Application.Features.Approvals.Commands.ReviewApproval;
using _1Rad.Application.Features.Finance.Overview;
using _1Rad.Application.Features.Hospitals.BillingSettings;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.Application.Features.Owner;

/// <summary>
/// The owner board: every centre the user owns (is an admin of), on one screen.
/// Each centre's figures come from that centre's own queries, run "as" that
/// centre (IActingCentre), so a number on the board is always the number the
/// centre's own Money ▸ Overview shows. Nothing here trusts the login token for
/// which centres are allowed: OwnerAccess checks the database every time.
/// </summary>
public record OwnerCentreInfo(Guid HospitalId, string Name, string? GroupName, bool IsMain, Guid? FollowsHospitalId, bool LinkBilling, bool LinkPrices);

public record OwnerCentreGlance(OwnerCentreInfo Centre, MoneyOverviewDto Money, int LateReports);

public record OwnerTotals(
    int Bills, decimal Billed, decimal Collected, decimal Due, decimal PartnerShare, decimal CentreKeeps,
    decimal Received, decimal ExpensesPaid,
    int Approvals, int OldDueBills, decimal OldDueAmount, int DaysNotClosed, int LateReports, int TdsNotSet);

public record OwnerGlanceDto(DateTime From, DateTime To, List<OwnerCentreGlance> Centres, OwnerTotals Totals);

public record GetOwnerGlanceQuery(DateTime From, DateTime To, int LateAfterMinutes = 180) : IRequest<OwnerGlanceDto>;

public record OwnerApprovalItem(
    Guid Id, Guid HospitalId, string Centre, string Type, string Title, string Reason, DateTime CreatedAt,
    string? AskedBy, bool AskedByMe, string? BillNumber, decimal? BillTotal, string? PatientName);

public record GetOwnerApprovalsQuery : IRequest<List<OwnerApprovalItem>>;

public record OwnerReviewApprovalCommand(Guid HospitalId, Guid Id, bool Approve, string? Note, string? RefundMode = null) : IRequest<bool>;

public record OwnerSetupCentre(OwnerCentreInfo Centre, BillingSettingsDto Billing, int PriceCount);
public record OwnerPriceRow(string Modality, string Test, Dictionary<Guid, decimal> Prices);
public record OwnerSetupDto(Guid? MainHospitalId, List<OwnerSetupCentre> Centres, List<OwnerPriceRow> Prices, bool PricesCut);

public record GetOwnerSetupQuery : IRequest<OwnerSetupDto>;

public record CopyOwnerSetupCommand(Guid FromHospitalId, List<Guid> ToHospitalIds, bool Billing, bool Prices, bool KeepLinked) : IRequest<List<ChainCopyResult>>;

public record UnlinkCentreCommand(Guid HospitalId) : IRequest<bool>;

internal static class OwnerCentres
{
    /// <summary>The owner's centres with their link state. "Main" = a centre others follow; with no links yet, the first centre.</summary>
    public static async Task<List<OwnerCentreInfo>> InfoAsync(IApplicationDbContext db, List<OwnedCentre> mine, CancellationToken ct)
    {
        var ids = mine.Select(c => c.HospitalId).ToList();
        var links = await db.HospitalSettingsLinks.IgnoreQueryFilters().AsNoTracking()
            .Where(l => ids.Contains(l.HospitalId) || ids.Contains(l.SourceHospitalId)).ToListAsync(ct);
        var sources = links.Select(l => l.SourceHospitalId).ToHashSet();
        Guid? fallbackMain = links.Count == 0 ? mine.FirstOrDefault()?.HospitalId : null;
        return mine.Select(c =>
        {
            var l = links.FirstOrDefault(x => x.HospitalId == c.HospitalId && ids.Contains(x.SourceHospitalId) && (x.LinkBilling || x.LinkPrices));
            return new OwnerCentreInfo(c.HospitalId, c.Name, c.GroupName,
                sources.Contains(c.HospitalId) || fallbackMain == c.HospitalId,
                l?.SourceHospitalId, l?.LinkBilling ?? false, l?.LinkPrices ?? false);
        }).ToList();
    }
}

public class OwnerBoardHandlers :
    IRequestHandler<GetOwnerGlanceQuery, OwnerGlanceDto>,
    IRequestHandler<GetOwnerApprovalsQuery, List<OwnerApprovalItem>>,
    IRequestHandler<OwnerReviewApprovalCommand, bool>,
    IRequestHandler<GetOwnerSetupQuery, OwnerSetupDto>,
    IRequestHandler<CopyOwnerSetupCommand, List<ChainCopyResult>>,
    IRequestHandler<UnlinkCentreCommand, bool>
{
    private const int MaxPriceRows = 3000;
    private readonly IApplicationDbContext _db;
    private readonly IMediator _mediator;
    private readonly IActingCentre _acting;

    public OwnerBoardHandlers(IApplicationDbContext db, IMediator mediator, IActingCentre acting)
    {
        _db = db;
        _mediator = mediator;
        _acting = acting;
    }

    private Guid Me => _db.UserContext.UserId;

    public async Task<OwnerGlanceDto> Handle(GetOwnerGlanceQuery q, CancellationToken ct)
    {
        var mine = await OwnerAccess.CentresAsync(_db, Me, ct);
        var info = await OwnerCentres.InfoAsync(_db, mine, ct);
        var rows = new List<OwnerCentreGlance>();
        foreach (var c in info)
        {
            using (_acting.Act(c.HospitalId))
            {
                var money = await _mediator.Send(new GetMoneyOverviewQuery(q.From, q.To), ct);
                var late = (await _mediator.Send(new GetOverdueAppointmentsQuery(q.LateAfterMinutes), ct)).Count(a => a.AcknowledgedAt == null);
                rows.Add(new OwnerCentreGlance(c, money, late));
            }
        }
        var m = rows.Select(r => r.Money).ToList();
        var totals = new OwnerTotals(
            m.Sum(x => x.Bills), m.Sum(x => x.Billed), m.Sum(x => x.Collected), m.Sum(x => x.Due),
            m.Sum(x => x.PartnerShare), m.Sum(x => x.CentreKeeps), m.Sum(x => x.Received), m.Sum(x => x.ExpensesPaid),
            m.Sum(x => x.Needs.Approvals), m.Sum(x => x.Needs.OldDueBills), m.Sum(x => x.Needs.OldDueAmount),
            m.Sum(x => x.Needs.DaysNotClosed), rows.Sum(r => r.LateReports), m.Count(x => x.Needs.TdsNotSet));
        var from = m.FirstOrDefault()?.From ?? q.From.Date;
        var to = m.FirstOrDefault()?.To ?? q.To.Date;
        return new OwnerGlanceDto(from, to, rows, totals);
    }

    public async Task<List<OwnerApprovalItem>> Handle(GetOwnerApprovalsQuery q, CancellationToken ct)
    {
        var mine = await OwnerAccess.CentresAsync(_db, Me, ct);
        var ids = mine.Select(c => c.HospitalId).ToList();
        if (ids.Count == 0) return new List<OwnerApprovalItem>();
        var rows = await _db.ApprovalRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(a => ids.Contains(a.HospitalId) && a.DeletedAt == null && a.Status == "PENDING")
            .OrderBy(a => a.CreatedAt).Take(200)
            .Select(a => new { a.Id, a.HospitalId, a.Type, a.Title, a.Reason, a.CreatedAt, a.RequestedBy, a.InvoiceId, a.AppointmentId })
            .ToListAsync(ct);
        var userIds = rows.Select(r => r.RequestedBy).Distinct().ToList();
        var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.UserId))
            .Select(u => new { u.UserId, u.FullName }).ToListAsync(ct);
        var invoiceIds = rows.Where(r => r.InvoiceId != null).Select(r => r.InvoiceId!.Value).Distinct().ToList();
        var bills = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => invoiceIds.Contains(i.Id) && ids.Contains(i.HospitalId))
            .Select(i => new { i.Id, Number = i.InvoiceId, i.TotalAmount, i.PatientName }).ToListAsync(ct);
        return rows.Select(r =>
        {
            var b = r.InvoiceId == null ? null : bills.FirstOrDefault(x => x.Id == r.InvoiceId);
            return new OwnerApprovalItem(r.Id, r.HospitalId, mine.First(c => c.HospitalId == r.HospitalId).Name,
                r.Type, r.Title, r.Reason, r.CreatedAt,
                names.FirstOrDefault(n => n.UserId == r.RequestedBy)?.FullName, r.RequestedBy == Me,
                b?.Number, b?.TotalAmount, b?.PatientName);
        }).ToList();
    }

    public async Task<bool> Handle(OwnerReviewApprovalCommand c, CancellationToken ct)
    {
        await OwnerAccess.RequireAsync(_db, Me, new[] { c.HospitalId }, ct);
        using (_acting.Act(c.HospitalId))
        {
            // The centre's own review: same checks (not your own request unless you
            // are the only admin), same effects, same audit trail.
            return await _mediator.Send(new ReviewApprovalCommand { Id = c.Id, Approve = c.Approve, Note = c.Note, RefundMode = c.RefundMode }, ct);
        }
    }

    public async Task<OwnerSetupDto> Handle(GetOwnerSetupQuery q, CancellationToken ct)
    {
        var mine = await OwnerAccess.CentresAsync(_db, Me, ct);
        var info = await OwnerCentres.InfoAsync(_db, mine, ct);
        var ids = info.Select(i => i.HospitalId).ToList();
        var prices = await _db.ServiceCharges.IgnoreQueryFilters().AsNoTracking()
            .Where(s => ids.Contains(s.HospitalId))
            .Select(s => new { s.HospitalId, s.Modality, s.ServiceName, s.Amount })
            .Take(MaxPriceRows * Math.Max(1, ids.Count) + 1)
            .ToListAsync(ct);

        var centres = new List<OwnerSetupCentre>();
        foreach (var c in info)
        {
            var billing = await _mediator.Send(new GetBillingSettingsQuery(c.HospitalId), ct);
            centres.Add(new OwnerSetupCentre(c, billing, prices.Count(p => p.HospitalId == c.HospitalId)));
        }

        var grouped = prices
            .GroupBy(p => ChainSetup.Key(p.Modality, p.ServiceName))
            .Select(g => new OwnerPriceRow(g.First().Modality, g.First().ServiceName,
                g.GroupBy(p => p.HospitalId).ToDictionary(x => x.Key, x => x.First().Amount)))
            .OrderBy(r => r.Modality).ThenBy(r => r.Test)
            .ToList();
        var cut = grouped.Count > MaxPriceRows;
        return new OwnerSetupDto(info.FirstOrDefault(i => i.IsMain)?.HospitalId, centres, cut ? grouped.Take(MaxPriceRows).ToList() : grouped, cut);
    }

    public async Task<List<ChainCopyResult>> Handle(CopyOwnerSetupCommand c, CancellationToken ct)
    {
        await OwnerAccess.RequireAsync(_db, Me, c.ToHospitalIds.Append(c.FromHospitalId), ct);
        var result = await ChainSetup.CopyAsync(_db, c.FromHospitalId, c.ToHospitalIds, c.Billing, c.Prices, c.KeepLinked, Me, ct);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<bool> Handle(UnlinkCentreCommand c, CancellationToken ct)
    {
        await OwnerAccess.RequireAsync(_db, Me, new[] { c.HospitalId }, ct);
        var link = await _db.HospitalSettingsLinks.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.HospitalId == c.HospitalId, ct);
        if (link == null) return false;
        _db.HospitalSettingsLinks.Remove(link);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
