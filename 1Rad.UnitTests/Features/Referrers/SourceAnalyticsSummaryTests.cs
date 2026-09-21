using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// The Source Analytics screen loads a SUMMARY (one row per source, every total, no visit rows)
/// and fetches a source's visits only when it is opened, a page at a time. The summary must agree
/// exactly with the full response it replaces, and a page must be a true slice of the source's visits.
/// </summary>
public class SourceAnalyticsSummaryTests : BaseHandlerTest
{
    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private Referrer AddReferrer(string name)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name };
        Context.Referrers.Add(r);
        return r;
    }

    private Appointment AddVisit(string? referredBy, Guid? referrerId, DateTime whenUtc, decimal billed = 0, decimal paid = 0, params string[] modalities)
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "P" + Guid.NewGuid().ToString()[..4] };
        Context.Patients.Add(patient);
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = whenUtc, Status = "CONFIRMED", ArrivedAt = whenUtc, Priority = "ROUTINE",
            ReferredBy = referredBy, ReferrerId = referrerId, Modality = modalities.FirstOrDefault() ?? "XRAY", Service = "S",
        };
        Context.Appointments.Add(a);
        foreach (var m in modalities)
            Context.AppointmentServices.Add(new AppointmentService { AppointmentId = a.AppointmentId, HospitalId = HospitalId, ServiceName = m + " scan", Modality = m, Amount = 100 });
        if (billed > 0)
            Context.Invoices.Add(new Invoice { Id = Guid.NewGuid(), InvoiceId = "I" + Guid.NewGuid().ToString()[..6], HospitalId = HospitalId, AppointmentId = a.AppointmentId, PatientId = patient.PatientId, GrossAmount = billed, TotalAmount = billed, PaidAmount = paid, Status = "PAID" });
        return a;
    }

    private Task<List<ReferrerIntelligenceDto>> Run(GetReferralIntelligenceQuery q) =>
        new GetReferralIntelligenceQueryHandler(Context, MockUserContext.Object).Handle(q, CancellationToken.None);

    private async Task Seed()
    {
        var a = AddReferrer("DR A");
        var b = AddReferrer("DR B");
        var first = AddVisit("DR A", a.ReferrerId, Day.AddHours(6), 900, 900, "CT", "USG");
        AddVisit("DR A", a.ReferrerId, Day.AddHours(7), 500, 200, "XRAY");
        AddVisit("DR A", a.ReferrerId, Day.AddDays(1).AddHours(6), 0, 0, "CT");
        AddVisit("DR B", b.ReferrerId, Day.AddHours(8), 300, 300, "USG");
        AddVisit("SELF", null, Day.AddHours(9), 200, 200, "XRAY");
        AddVisit("DR GHOST", null, Day.AddHours(10), 0, 0, "XRAY");     // named, no partner record
        AddVisit(null, null, Day.AddHours(11), 0, 0, "XRAY");           // nobody recorded
        Context.ReferralCommissions.Add(new ReferralCommission
        {
            ReferrerId = a.ReferrerId, ReferrerName = "DR A", HospitalId = HospitalId, AppointmentId = first.AppointmentId, Modality = "CT", CommissionAmount = 150m,
            Status = "UNPAID", TransactionDate = Day, ServiceDate = Day.AddHours(6),
        });
        await Context.SaveChangesAsync();
    }

    [Fact]
    public async Task TheSummary_CarriesEveryTotalOfTheFullResponse_ButNoVisitRows()
    {
        await Seed();

        var full = await Run(new GetReferralIntelligenceQuery());
        var summary = await Run(new GetReferralIntelligenceQuery(SummaryOnly: true));

        Assert.Equal(full.Select(n => n.SourceKey), summary.Select(n => n.SourceKey));
        Assert.All(summary, n => Assert.Empty(n.Patients));
        Assert.All(full, n => Assert.Equal(n.TotalPatients, n.Patients.Count));
        foreach (var f in full)
        {
            var s = summary.Single(n => n.SourceKey == f.SourceKey);
            Assert.Equal(f.Name, s.Name);
            Assert.Equal(f.SourceKind, s.SourceKind);
            Assert.Equal(f.TotalPatients, s.TotalPatients);
            Assert.Equal(f.UniquePatients, s.UniquePatients);
            Assert.Equal(f.NewPatients, s.NewPatients);
            Assert.Equal(f.ReturningVisits, s.ReturningVisits);
            Assert.Equal(f.TotalRevenue, s.TotalRevenue);
            Assert.Equal(f.TotalCollected, s.TotalCollected);
            Assert.Equal(f.TotalDiscount, s.TotalDiscount);
            Assert.Equal(f.NetProfit, s.NetProfit);
            Assert.Equal(f.TotalCommission, s.TotalCommission);
            Assert.Equal(f.PaidCommission, s.PaidCommission);
            Assert.Equal(f.UnpaidCommission, s.UnpaidCommission);
            Assert.Equal(f.BookedPending, s.BookedPending);
            Assert.Equal(f.NoShows, s.NoShows);
        }
    }

    [Fact]
    public async Task EverySourceKindGetsAKey_AndAPartnersKeyIsItsId()
    {
        await Seed();

        var summary = await Run(new GetReferralIntelligenceQuery(SummaryOnly: true));

        var partners = summary.Where(n => n.SourceKind == "PARTNER").ToList();
        Assert.All(partners, n => Assert.Equal(n.ReferrerId.ToString(), n.SourceKey));
        Assert.Equal("self", summary.Single(n => n.SourceKind == "SELF").SourceKey);
        Assert.Equal("unattributed", summary.Single(n => n.SourceKind == "UNATTRIBUTED").SourceKey);
        Assert.Equal("name:DR GHOST", summary.Single(n => n.SourceKind == "UNLINKED").SourceKey);
    }

    [Fact]
    public async Task TheModalityMix_IsCountedPerServiceLine_OnTheSummary()
    {
        await Seed();

        var summary = await Run(new GetReferralIntelligenceQuery(SummaryOnly: true));

        var drA = summary.Single(n => n.Name == "DR A");
        // Visits: CT+USG, XRAY, CT  ->  CT 2, USG 1, XRAY 1 (a two-line visit counts once in each).
        Assert.Equal(2, drA.Modalities!["CT"]);
        Assert.Equal(1, drA.Modalities["USG"]);
        Assert.Equal(1, drA.Modalities["XRAY"]);
    }

    // ── the drill-down ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OpeningASource_ReturnsItsVisitsNewestFirst_WithTotalsForTheWholeSource()
    {
        await Seed();
        var drA = (await Run(new GetReferralIntelligenceQuery(SummaryOnly: true))).Single(n => n.Name == "DR A");

        var node = Assert.Single(await Run(new GetReferralIntelligenceQuery(SourceKey: drA.SourceKey, Take: 100)));

        Assert.Equal(3, node.Patients.Count);
        Assert.Equal(drA.TotalPatients, node.TotalPatients);
        Assert.Equal(drA.TotalRevenue, node.TotalRevenue);
        Assert.Equal(node.Patients.Select(p => p.VisitAt).OrderByDescending(x => x), node.Patients.Select(p => p.VisitAt));
        // Per-visit commission still resolves for the rows that are returned.
        Assert.Equal(150m, node.Patients.Sum(p => p.CommissionAmount));
    }

    [Fact]
    public async Task Paging_SlicesTheSameOrderedVisits_AndTotalsNeverShrinkWithThePage()
    {
        await Seed();
        var drA = (await Run(new GetReferralIntelligenceQuery(SummaryOnly: true))).Single(n => n.Name == "DR A");

        var whole = Assert.Single(await Run(new GetReferralIntelligenceQuery(SourceKey: drA.SourceKey))).Patients;
        var page1 = Assert.Single(await Run(new GetReferralIntelligenceQuery(SourceKey: drA.SourceKey, Skip: 0, Take: 2)));
        var page2 = Assert.Single(await Run(new GetReferralIntelligenceQuery(SourceKey: drA.SourceKey, Skip: 2, Take: 2)));

        Assert.Equal(2, page1.Patients.Count);
        Assert.Single(page2.Patients);
        Assert.Equal(whole.Select(p => p.AppointmentId), page1.Patients.Concat(page2.Patients).Select(p => p.AppointmentId));
        Assert.Equal(3, page1.TotalPatients);
        Assert.Equal(3, page2.TotalPatients);
        Assert.Equal(drA.TotalRevenue, page2.TotalRevenue);
        Assert.Equal(drA.TotalCollected, page2.TotalCollected);
    }

    [Theory]
    [InlineData("self", "SELF")]
    [InlineData("unattributed", "UNATTRIBUTED")]
    [InlineData("name:DR GHOST", "UNLINKED")]
    [InlineData("NAME:dr ghost", "UNLINKED")]        // the key is matched case-insensitively
    public async Task TheDrillDown_WorksForSelfUnlinkedAndUnattributedSources_TooAsync(string key, string kind)
    {
        await Seed();

        var node = Assert.Single(await Run(new GetReferralIntelligenceQuery(SourceKey: key, Take: 50)));

        Assert.Equal(kind, node.SourceKind);
        Assert.Single(node.Patients);
    }

    [Fact]
    public async Task TheDrillDown_ForAKeyThatMatchesNothing_ReturnsNoSource()
    {
        await Seed();

        Assert.Empty(await Run(new GetReferralIntelligenceQuery(SourceKey: Guid.NewGuid().ToString(), Take: 50)));
        Assert.Empty(await Run(new GetReferralIntelligenceQuery(SourceKey: "name:NOBODY", Take: 50)));
    }
}
