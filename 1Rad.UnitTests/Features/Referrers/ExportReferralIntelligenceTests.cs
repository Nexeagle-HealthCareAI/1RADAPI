using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Referrers.Queries.ExportReferralIntelligence;
using _1Rad.Domain.Entities;
using ClosedXML.Excel;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// The Excel export is downloaded FROM the Referrals screen and must count exactly what the screen
/// counts: only attended visits, the same source labels (merge-resolved, Self/Unattributed
/// canonical), in the same IST range. It used to include every booked/no-show visit and label a
/// blank referrer differently from a literal "Self", so the file disagreed with the screen it came from.
/// </summary>
public class ExportReferralIntelligenceTests : BaseHandlerTest
{
    private static readonly DateTime Day = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    private Referrer AddReferrer(string name, Guid? mergedInto = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, MergedIntoId = mergedInto };
        Context.Referrers.Add(r);
        return r;
    }

    private Appointment AddVisit(string? referredBy, Guid? referrerId, string status, DateTime whenUtc, DateTime? arrivedAt = null, string modality = "XRAY")
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "P" + Guid.NewGuid().ToString()[..4] };
        Context.Patients.Add(patient);
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = whenUtc, Status = status, ArrivedAt = arrivedAt, Priority = "ROUTINE",
            ReferredBy = referredBy, ReferrerId = referrerId, Modality = modality, Service = modality + " scan",
        };
        Context.Appointments.Add(a);
        return a;
    }

    private static List<(string Referrer, string Status)> ReadRows(byte[] xlsx)
    {
        using var wb = new XLWorkbook(new MemoryStream(xlsx));
        var ws = wb.Worksheet(1);
        var rows = new List<(string, string)>();
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (var r = 2; r <= last; r++)
            rows.Add((ws.Cell(r, 1).GetString(), ws.Cell(r, 6).GetString()));
        return rows;
    }

    private Task<byte[]> Export(DateTime? from = null, DateTime? to = null, bool allTime = false) =>
        new ExportReferralIntelligenceQueryHandler(Context)
            .Handle(new ExportReferralIntelligenceQuery(from, to, allTime), CancellationToken.None);

    [Fact]
    public async Task OnlyAttendedVisits_AreExported_NotBookedAheadOrNoShows()
    {
        var dr = AddReferrer("DR A");
        AddVisit("DR A", dr.ReferrerId, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));   // attended
        AddVisit("DR A", dr.ReferrerId, "BOOKED", DateTime.UtcNow.AddDays(1));                        // still ahead
        AddVisit("DR A", dr.ReferrerId, "BOOKED", DateTime.UtcNow.AddDays(-3));                       // past, never arrived: no-show
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Single(rows);
    }

    [Fact]
    public async Task ALiteralSelfInAnyCasing_GetsTheSameCanonicalLabel_DistinctFromABlankReferrer()
    {
        // A blank ReferredBy means no referrer was ever recorded - a data gap (Unattributed), NOT the
        // same thing as an explicit "Self" walk-in. The export used to conflate the two into one
        // "Direct / Walk-in" bucket, hiding exactly the gap Source Analytics tracks.
        AddVisit(null, null, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        AddVisit("Self", null, "CONFIRMED", Day.AddHours(7), arrivedAt: Day.AddHours(7));
        AddVisit("SELF", null, "CONFIRMED", Day.AddHours(8), arrivedAt: Day.AddHours(8));
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Referrer == "SELF / WALK-IN"));
        Assert.Single(rows, r => r.Referrer == "UNATTRIBUTED");
    }

    [Fact]
    public async Task AMergedDuplicatesVisits_AreReportedUnderThePrimary()
    {
        var primary = AddReferrer("DR PRIMARY");
        var dup = AddReferrer("DR DUP", mergedInto: primary.ReferrerId);
        AddVisit("DR PRIMARY", primary.ReferrerId, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        AddVisit("DR DUP", dup.ReferrerId, "CONFIRMED", Day.AddHours(7), arrivedAt: Day.AddHours(7));
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("DR PRIMARY", r.Referrer));
    }

    [Fact]
    public async Task ARenamedPartnersVisit_IsLabelledByItsCurrentName_EvenWhenTheVisitCarriesTheOldText()
    {
        var dr = AddReferrer("DR NEW NAME");
        AddVisit("DR OLD NAME", dr.ReferrerId, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Equal("DR NEW NAME", Assert.Single(rows).Referrer);
    }

    [Fact]
    public async Task ANamedReferrerWithNoPartnerRecord_KeepsItsTypedName_DistinctFromUnattributed()
    {
        AddVisit("DR GHOST", null, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));    // typed, no record
        AddVisit(null, null, "CONFIRMED", Day.AddHours(7), arrivedAt: Day.AddHours(7));           // nothing recorded
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Contains(rows, r => r.Referrer == "DR GHOST");
        Assert.Contains(rows, r => r.Referrer == "UNATTRIBUTED");
    }

    [Fact]
    public async Task ARowMatchesEverySourceAnalyticsCount_ForTheSameRange()
    {
        var dr = AddReferrer("DR A");
        AddVisit("DR A", dr.ReferrerId, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        AddVisit("DR A", dr.ReferrerId, "DELIVERED", Day.AddHours(7), arrivedAt: Day.AddHours(7));
        AddVisit("DR A", dr.ReferrerId, "BOOKED", Day.AddDays(1).AddHours(6));                    // out of range anyway
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(Day, Day));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("DR A", r.Referrer));
    }

    [Fact]
    public async Task ACancelledVisit_IsNeverExported()
    {
        AddVisit("DR A", null, "CANCELLED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        await Context.SaveChangesAsync();

        Assert.Empty(ReadRows(await Export(allTime: true)));
    }

    [Fact]
    public async Task AMultiServiceVisit_ExportsOneRowPerServiceLine()
    {
        var visit = AddVisit("DR A", null, "CONFIRMED", Day.AddHours(6), arrivedAt: Day.AddHours(6));
        Context.AppointmentServices.Add(new AppointmentService { AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "CT Head", Modality = "CT", Amount = 100 });
        Context.AppointmentServices.Add(new AppointmentService { AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "USG Abdomen", Modality = "USG", Amount = 100 });
        await Context.SaveChangesAsync();

        var rows = ReadRows(await Export(allTime: true));

        Assert.Equal(2, rows.Count);
    }
}
