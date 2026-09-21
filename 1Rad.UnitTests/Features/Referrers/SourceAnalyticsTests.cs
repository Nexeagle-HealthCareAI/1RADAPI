using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Source Analytics: every visit lands in exactly one, correctly-labelled source; only visits
/// where the patient arrived count; dates are IST; new-vs-returning and billed-vs-collected are
/// real figures; and the Volume Matrix agrees with it.
/// </summary>
public class SourceAnalyticsTests : BaseHandlerTest
{
    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private Referrer AddReferrer(string name, Guid? mergedInto = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, MergedIntoId = mergedInto };
        Context.Referrers.Add(r);
        return r;
    }

    private Patient AddPatient(Guid? referrerId = null)
    {
        var p = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "P" + Guid.NewGuid().ToString()[..4], ReferrerId = referrerId };
        Context.Patients.Add(p);
        return p;
    }

    private Appointment AddVisit(Patient patient, string? referredBy, DateTime whenUtc, string status = "CONFIRMED", DateTime? arrivedAt = null)
    {
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = whenUtc, Status = status, Priority = "ROUTINE", ReferredBy = referredBy, ArrivedAt = arrivedAt,
        };
        Context.Appointments.Add(a);
        return a;
    }

    private Appointment AddVisit(string? referredBy, DateTime whenUtc, string status = "CONFIRMED") => AddVisit(AddPatient(), referredBy, whenUtc, status);

    private GetReferralIntelligenceQueryHandler Handler() => new(Context, MockUserContext.Object);

    private Task<List<ReferrerIntelligenceDto>> Run(DateTime? from = null, DateTime? to = null) =>
        Handler().Handle(new GetReferralIntelligenceQuery(from, to), CancellationToken.None);

    // ── every visit lands somewhere, correctly labelled ─────────────────────────

    [Fact]
    public async Task Sources_AreLabelledByKind_SoSelfIsNeverConfusedWithAnUnlinkedNameOrMissingData()
    {
        var self = AddReferrer("Self");
        var dr = AddReferrer("DR REAL");
        AddVisit("Self", Day.AddHours(5));
        AddVisit("SELF", Day.AddHours(5));
        AddVisit("DR GHOST", Day.AddHours(6));        // typed name, no partner record
        AddVisit("DR REAL", Day.AddHours(6));
        AddVisit(null, Day.AddHours(6));              // nothing recorded at all
        AddVisit("  ", Day.AddHours(6));
        await Context.SaveChangesAsync();

        var nodes = await Run();

        var byKind = nodes.ToDictionary(n => n.SourceKind);
        Assert.Equal(4, nodes.Count);
        Assert.Equal(2, byKind["SELF"].TotalPatients);
        Assert.Equal("Self / Walk-in", byKind["SELF"].Name);
        Assert.Equal(1, byKind["UNLINKED"].TotalPatients);
        Assert.Equal("DR GHOST", byKind["UNLINKED"].Name);
        Assert.Equal(2, byKind["UNATTRIBUTED"].TotalPatients);
        Assert.Equal(dr.ReferrerId, byKind["PARTNER"].ReferrerId);
        // Only a real partner carries an id - the others must be told apart by SourceKind.
        Assert.All(nodes.Where(n => n.SourceKind != "PARTNER"), n => Assert.Equal(Guid.Empty, n.ReferrerId));
        // Nothing is dropped: the sources add up to every attended visit.
        Assert.Equal(6, nodes.Sum(n => n.TotalPatients));
    }

    [Fact]
    public async Task ANamedReferrerWithNoRecord_FallsBackToThePatientsLinkedPartner_BeforeBeingCalledUnlinked()
    {
        var dr = AddReferrer("DR LINKED");
        AddVisit(AddPatient(dr.ReferrerId), "DR TYPO", Day.AddHours(6));
        await Context.SaveChangesAsync();

        var node = Assert.Single(await Run());

        Assert.Equal("PARTNER", node.SourceKind);
        Assert.Equal("DR LINKED", node.Name);
    }

    // ── attendance ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyVisitsWhereThePatientArrivedCount_BookedAndNoShowsAreReportedSeparately()
    {
        var dr = AddReferrer("DR A");
        AddVisit("DR A", DateTime.UtcNow.AddHours(-3), "CONFIRMED");                  // arrived
        AddVisit("DR A", DateTime.UtcNow.AddHours(-2), "DELIVERED");                  // arrived, report delivered
        AddVisit(AddPatient(), "DR A", DateTime.UtcNow.AddHours(-4), "BOOKED", arrivedAt: DateTime.UtcNow.AddHours(-4));   // ArrivedAt wins over a stale status
        AddVisit("DR A", DateTime.UtcNow.AddDays(1), "BOOKED");                       // still ahead
        AddVisit("DR A", DateTime.UtcNow.AddDays(-3), "BOOKED");                      // past and never came = no-show
        AddVisit("DR A", DateTime.UtcNow.AddDays(-2), "scheduled");
        AddVisit("DR A", DateTime.UtcNow.AddHours(-1), "CANCELLED");                  // not referral business at all
        await Context.SaveChangesAsync();

        var node = Assert.Single(await Run());

        Assert.Equal(3, node.TotalPatients);
        Assert.Equal(3, node.Patients.Count);
        Assert.Equal(1, node.BookedPending);
        Assert.Equal(2, node.NoShows);
    }

    [Fact]
    public void Attendance_Classification()
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("ATTENDED", AppointmentAttendance.Classify("in_progress", null, now.AddDays(-9), now));
        Assert.Equal("ATTENDED", AppointmentAttendance.Classify("IN PROGRESS", null, now, now));
        Assert.Equal("ATTENDED", AppointmentAttendance.Classify("BOOKED", now, now, now));
        Assert.Equal("UPCOMING", AppointmentAttendance.Classify("BOOKED", null, now.AddHours(3), now));      // later today
        Assert.Equal("UPCOMING", AppointmentAttendance.Classify(null, null, now.AddDays(2), now));
        Assert.Equal("NO_SHOW", AppointmentAttendance.Classify("BOOKED", null, now.AddDays(-1), now));
        // 23:30 IST is still "today" while it is 22:30 IST (17:00 UTC) - and a NO_SHOW once the IST day has rolled over.
        var lateSlot = new DateTime(2026, 6, 15, 18, 0, 0, DateTimeKind.Utc);
        Assert.Equal("UPCOMING", AppointmentAttendance.Classify("BOOKED", null, lateSlot, new DateTime(2026, 6, 15, 17, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("NO_SHOW", AppointmentAttendance.Classify("BOOKED", null, lateSlot, new DateTime(2026, 6, 15, 20, 0, 0, DateTimeKind.Utc)));
    }

    // ── IST dates and time of day ───────────────────────────────────────────────

    [Fact]
    public async Task VisitDatesAreIst_AndCarryTheTimeOfDay()
    {
        AddReferrer("DR A");
        AddVisit("DR A", Day.AddHours(19));      // 19:00 UTC = 00:30 IST on the 16th
        AddVisit("DR A", Day.AddHours(3));       // 03:00 UTC = 08:30 IST on the 15th
        await Context.SaveChangesAsync();

        var rows = Assert.Single(await Run()).Patients;

        Assert.Contains(rows, r => r.RegistrationDate == "2026-06-16" && r.VisitAt == "2026-06-16T00:30");
        Assert.Contains(rows, r => r.RegistrationDate == "2026-06-15" && r.VisitAt == "2026-06-15T08:30");
    }

    // ── new vs returning ────────────────────────────────────────────────────────

    [Fact]
    public async Task NewAndReturningAreRealPatientFacts_NotNameMatching()
    {
        AddReferrer("DR A");
        AddReferrer("DR B");

        // Patient 1: first ever attended visit is with Dr B, LAST MONTH -> the visit in range is a repeat.
        var p1 = AddPatient();
        AddVisit(p1, "DR B", Day.AddDays(-30));
        AddVisit(p1, "DR A", Day.AddHours(6));
        // Patient 2: first ever visit is in range -> new. Also comes back the same week -> a repeat visit.
        var p2 = AddPatient();
        AddVisit(p2, "DR A", Day.AddHours(5));
        AddVisit(p2, "DR A", Day.AddDays(3));
        // Patient 3: an earlier BOOKING that never happened and a CANCELLED one do not make the in-range visit a repeat.
        var p3 = AddPatient();
        AddVisit(p3, "DR A", Day.AddDays(-20), "BOOKED");
        AddVisit(p3, "DR A", Day.AddDays(-10), "CANCELLED");
        AddVisit(p3, "DR A", Day.AddHours(7));
        // Two DIFFERENT people who happen to share a name are two patients, not one "repeat".
        var twinA = AddPatient(); twinA.FullName = "RAM KUMAR";
        var twinB = AddPatient(); twinB.FullName = "RAM KUMAR";
        AddVisit(twinA, "DR A", Day.AddHours(8));
        AddVisit(twinB, "DR A", Day.AddHours(9));
        await Context.SaveChangesAsync();

        var node = (await Run(Day, Day.AddDays(7))).Single(n => n.Name == "DR A");

        Assert.Equal(6, node.TotalPatients);        // visits in range: p1, p2 x2, p3, twinA, twinB
        Assert.Equal(5, node.UniquePatients);       // p1, p2, p3 and the two RAM KUMARs
        Assert.Equal(4, node.NewPatients);          // p2, p3 and both twins (p1 was first seen elsewhere, earlier)
        Assert.Equal(2, node.ReturningVisits);      // p1's visit, and p2's second visit = 6 - 4
    }

    // ── billed vs collected ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReportsCashCollectedSeparatelyFromWhatWasBilled()
    {
        AddReferrer("DR A");
        var v1 = AddVisit("DR A", Day.AddHours(6));
        var v2 = AddVisit("DR A", Day.AddHours(7));
        Context.Invoices.Add(new Invoice { Id = Guid.NewGuid(), InvoiceId = "I1", HospitalId = HospitalId, AppointmentId = v1.AppointmentId, PatientId = v1.PatientId, GrossAmount = 1000, DiscountAmount = 100, TotalAmount = 900, PaidAmount = 900, Status = "PAID" });
        Context.Invoices.Add(new Invoice { Id = Guid.NewGuid(), InvoiceId = "I2", HospitalId = HospitalId, AppointmentId = v2.AppointmentId, PatientId = v2.PatientId, GrossAmount = 500, TotalAmount = 500, PaidAmount = 200, Status = "PARTIAL" });
        await Context.SaveChangesAsync();

        var node = Assert.Single(await Run());

        Assert.Equal(1400m, node.TotalRevenue);      // billed
        Assert.Equal(1100m, node.TotalCollected);    // cash
        Assert.Equal(100m, node.TotalDiscount);
        Assert.Contains(node.Patients, p => p.TotalAmount == 500m && p.PaidAmount == 200m);
    }

    // ── the matrix agrees ───────────────────────────────────────────────────────

    [Fact]
    public async Task VolumeMatrix_UsesTheSameAttributionAndAttendanceAsSourceAnalytics()
    {
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE", mergedInto: primary.ReferrerId);
        AddReferrer("Self");
        AddVisit("DR PRIMARY", Day.AddHours(12).AddMinutes(30));         // 18:00 IST -> Evening
        AddVisit("DR DUPE", Day.AddHours(3));                             // 08:30 IST -> Morning, rolls into the primary
        AddVisit("Self", Day.AddHours(5));
        AddVisit("DR GHOST", Day.AddHours(5));
        AddVisit(null, Day.AddHours(5));
        AddVisit("DR PRIMARY", Day.AddDays(3), "BOOKED");                 // not arrived - excluded
        await Context.SaveChangesAsync();

        var matrix = await new GetReferralMatrixQueryHandler(Context)
            .Handle(new GetReferralMatrixQuery("DAY", Day, 1), CancellationToken.None);

        var byKind = matrix.Rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.ToList());
        var partner = Assert.Single(byKind["PARTNER"]);
        Assert.Equal("DR PRIMARY", partner.Name);
        Assert.Equal(2, partner.Total);
        Assert.Equal(1, partner.Counts["Evening (5pm-12am)"]);
        Assert.Equal(1, partner.Counts["Morning (12am-12pm)"]);
        Assert.Equal(1, Assert.Single(byKind["SELF"]).Total);
        Assert.Equal("DR GHOST", Assert.Single(byKind["UNLINKED"]).Name);
        Assert.Equal(1, Assert.Single(byKind["UNATTRIBUTED"]).Total);

        // ...and the same day through Source Analytics reports the same volume per source.
        var nodes = await Run(Day, Day);
        Assert.Equal(matrix.Rows.Sum(r => r.Total), nodes.Sum(n => n.TotalPatients));
    }

    // ── the attribution rules themselves ────────────────────────────────────────

    [Fact]
    public void Attribution_PrefersALiveRecordOverADeletedOneOfTheSameName_AndResolvesMerges()
    {
        var deleted = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR SAME", null, null, DateTime.UtcNow);
        var live = new ReferralAttribution.Entry(Guid.NewGuid(), null, "DR SAME", null, null, null);
        var dup = new ReferralAttribution.Entry(Guid.NewGuid(), live.ReferrerId, "DR DUP", null, null, null);
        var a = new ReferralAttribution(new[] { deleted, live, dup });

        Assert.Equal(live.ReferrerId, a.Attribute("dr same", null).RootId);
        Assert.Equal(live.ReferrerId, a.Attribute("DR DUP", null).RootId);                 // merged duplicate -> primary
        Assert.Equal(SourceKind.Self, a.Attribute("self", null).Kind);
        Assert.Equal(SourceKind.Unattributed, a.Attribute(null, null).Kind);
        Assert.Equal(SourceKind.Unlinked, a.Attribute("Nobody", Guid.NewGuid()).Kind);      // patient link points at no partner either
        Assert.Equal(new[] { live.ReferrerId, dup.ReferrerId }.OrderBy(x => x), a.AliasIdsOf(dup.ReferrerId).OrderBy(x => x));
    }
}
