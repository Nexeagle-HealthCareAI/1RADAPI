using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Referrers.Commands.CreateReferrer;
using _1Rad.Application.Features.Referrers.Commands.MergeReferrers;
using _1Rad.Application.Features.Referrers.Queries.GetReferrersOutstanding;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Referral review round 5: the partner a visit credits (#13, #25), merged duplicates'
/// links (#22) and the server-side "who is owed what" list (#27).
/// </summary>
public class ReferralPartnerIdentityTests : BaseHandlerTest
{
    private Referrer AddReferrer(string name, string? contact = null, Guid? mergedInto = null, bool deleted = false, bool isDoctor = true)
    {
        var r = new Referrer
        {
            ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, Contact = contact, MergedIntoId = mergedInto,
            DeletedAt = deleted ? DateTime.UtcNow : null, IsDoctor = isDoctor,
        };
        Context.Referrers.Add(r);
        return r;
    }

    private Appointment AddVisit()
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "Pat" };
        Context.Patients.Add(patient);
        var a = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = "Pat",
            DateTime = DateTime.UtcNow, Status = "CONFIRMED", Priority = "ROUTINE",
        };
        Context.Appointments.Add(a);
        return a;
    }

    private void AddCommission(Referrer r, Appointment? a, decimal amount, string status = "UNPAID")
        => Context.ReferralCommissions.Add(new ReferralCommission
        {
            Id = Guid.NewGuid(), HospitalId = HospitalId, ReferrerId = r.ReferrerId, ReferrerName = r.Name ?? "",
            AppointmentId = a?.AppointmentId, Modality = "CT", CommissionAmount = amount, Status = status,
            TransactionDate = DateTime.UtcNow, ServiceDate = DateTime.UtcNow,
        });

    private void AddBill(Appointment a, decimal total, decimal paid)
        => Context.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(), InvoiceId = "INV-" + Guid.NewGuid().ToString()[..6], HospitalId = HospitalId, AppointmentId = a.AppointmentId,
            PatientId = a.PatientId, GrossAmount = total, TotalAmount = total, PaidAmount = paid, Status = paid >= total ? "PAID" : "PENDING",
        });

    // ── resolver ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolver_ThePickedPartnerWins_OverANamesake()
    {
        var doctor = AddReferrer("DR RAJESH KUMAR", "9800000001");
        var agent = AddReferrer("RAJESH KUMAR", "9800000002", isDoctor: false);
        await Context.SaveChangesAsync();

        var r = await ReferrerResolver.ResolveAsync(Context, HospitalId, agent.ReferrerId, "Dr Rajesh Kumar", null, CancellationToken.None);

        Assert.Equal(agent.ReferrerId, r!.ReferrerId);
        Assert.NotEqual(doctor.ReferrerId, r.ReferrerId);
    }

    [Fact]
    public async Task Resolver_AMergedDuplicateCreditsItsPrimary_AndADeletedPickFallsBackToTheName()
    {
        var primary = AddReferrer("DR A SHARMA");
        var dupe = AddReferrer("DR A. SHARMA", mergedInto: primary.ReferrerId);
        var gone = AddReferrer("DR GONE", deleted: true);
        var live = AddReferrer("DR B VERMA");
        await Context.SaveChangesAsync();

        Assert.Equal(primary.ReferrerId, (await ReferrerResolver.ResolveAsync(Context, HospitalId, dupe.ReferrerId, null, null, CancellationToken.None))!.ReferrerId);
        Assert.Equal(live.ReferrerId, (await ReferrerResolver.ResolveAsync(Context, HospitalId, gone.ReferrerId, "Dr B Verma", null, CancellationToken.None))!.ReferrerId);
    }

    [Fact]
    public async Task Resolver_TwoNamesakes_AreOnlyToldApartByMobile_NeverGuessed()
    {
        var one = AddReferrer("DR R KUMAR", "9800000001");
        AddReferrer("R. KUMAR", "9800000002");
        await Context.SaveChangesAsync();

        Assert.Null(await ReferrerResolver.ResolveAsync(Context, HospitalId, null, "R Kumar", null, CancellationToken.None));
        Assert.Equal(one.ReferrerId, (await ReferrerResolver.ResolveAsync(Context, HospitalId, null, "R Kumar", "+91 98000 00001", CancellationToken.None))!.ReferrerId);
    }

    [Fact]
    public async Task Resolver_SameMobileAndSurname_IsTheSamePerson_ButASharedClinicNumberIsNot()
    {
        var rajesh = AddReferrer("DR RAJESH KUMAR", "9800000009");
        await Context.SaveChangesAsync();

        Assert.Equal(rajesh.ReferrerId, (await ReferrerResolver.ResolveAsync(Context, HospitalId, null, "R. Kumar", "09800000009", CancellationToken.None))!.ReferrerId);
        Assert.Null(await ReferrerResolver.ResolveAsync(Context, HospitalId, null, "Dr Anita Singh", "9800000009", CancellationToken.None));
    }

    // ── adding partners ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePartner_SameNameOnAnotherMobile_IsNotFoldedIntoTheExistingOne()
    {
        var existing = AddReferrer("DR RAJESH KUMAR", "9800000001");
        existing.Specialty = "RADIOLOGY";
        await Context.SaveChangesAsync();
        var handler = new CreateReferrerCommandHandler(Context);

        // Exact same name, different number: refused with a clear reason (names are unique).
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(
            new CreateReferrerCommand("Dr Rajesh Kumar", "9800000002", ""), CancellationToken.None));

        // Same person (same number): reused, and a filled profile is not overwritten.
        var again = await handler.Handle(new CreateReferrerCommand("Rajesh Kumar", "9800000001", "", Specialty: "ORTHO"), CancellationToken.None);
        Assert.Equal(existing.ReferrerId, again);
        Assert.Equal("RADIOLOGY", Context.Referrers.Single(r => r.ReferrerId == existing.ReferrerId).Specialty);
    }

    // ── merging ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Merge_RevokesTheDuplicatesLinks_NotThePrimarys()
    {
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE");
        await Context.SaveChangesAsync();

        await new MergeReferrersCommandHandler(Context).Handle(new MergeReferrersCommand(dupe.ReferrerId, primary.ReferrerId), CancellationToken.None);

        Assert.Equal(1, await ReferralLinkVersions.GetOneAsync(Context, dupe.ReferrerId, CancellationToken.None));
        Assert.Equal(0, await ReferralLinkVersions.GetOneAsync(Context, primary.ReferrerId, CancellationToken.None));
    }

    // ── who is owed what ────────────────────────────────────────────────────────

    [Fact]
    public async Task Outstanding_SplitsPayableAwaitingAndDeficit_AndRollsUpDuplicates()
    {
        // The "only after the patient pays" rule (centre setting, migration 118).
        Context.HospitalBillingSettings.Add(new HospitalBillingSettings { HospitalId = HospitalId, PartnerPayWhenBilled = false });
        var primary = AddReferrer("DR A");
        var dupe = AddReferrer("DR A.", mergedInto: primary.ReferrerId);
        var paidVisit = AddVisit(); AddBill(paidVisit, 1000m, 1000m);
        var openVisit = AddVisit(); AddBill(openVisit, 1000m, 100m);   // part-paid: not payable yet
        AddCommission(primary, paidVisit, 200m);
        AddCommission(dupe, openVisit, 150m);
        AddCommission(dupe, null, -50m);                                // clawback owed back
        AddCommission(primary, null, 70m, "PAID");                      // already paid: not open
        var quiet = AddReferrer("DR QUIET");
        await Context.SaveChangesAsync();

        var result = await new GetReferrersOutstandingQueryHandler(Context).Handle(new GetReferrersOutstandingQuery(), CancellationToken.None);

        var row = Assert.Single(result.Items);
        Assert.Equal(primary.ReferrerId, row.ReferrerId);
        Assert.Equal(200m, row.PayableNow);
        Assert.Equal(150m, row.AwaitingPatient);
        Assert.Equal(50m, row.Deficit);
        Assert.Equal(1, row.AliasCount);
        Assert.Equal(1, result.Totals.PartnersToPay);

        var all = await new GetReferrersOutstandingQueryHandler(Context).Handle(new GetReferrersOutstandingQuery(Show: "all"), CancellationToken.None);
        Assert.Contains(all.Items, r => r.ReferrerId == quiet.ReferrerId);
        Assert.DoesNotContain(all.Items, r => r.ReferrerId == dupe.ReferrerId);
    }
}
