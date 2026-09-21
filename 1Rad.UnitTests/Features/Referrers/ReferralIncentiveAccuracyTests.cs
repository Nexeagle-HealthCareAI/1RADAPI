using System;
using _1Rad.Domain.Exceptions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Common;
using _1Rad.Application.Features.Appointments.Commands.ChangeReferrer;
using _1Rad.Application.Features.Referrers.Commands.DeleteReferrer;
using _1Rad.Application.Features.Referrers.Commands.MergeReferrers;
using _1Rad.Application.Features.Referrers.Commands.PayReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;
using _1Rad.Application.Features.Referrers.Queries.GetDetailedReferralLedger;
using _1Rad.Application.Features.Referrers.Queries.GetDoctorPortal;
using _1Rad.Application.Features.Referrers.Queries.GetReferralCommissions;
using _1Rad.Application.Features.Referrers.Queries.GetReferralMatrix;
using _1Rad.Application.Features.Referrers.Queries.GetReferralIntelligence;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Regression coverage for the referral-incentive accuracy pass: attribution,
/// partner-level money, payout gating/atomicity, merge/delete safety and the
/// doctor portal. Each test names the failure it prevents.
/// </summary>
public class ReferralIncentiveAccuracyTests : BaseHandlerTest
{
    // ── seeding helpers ─────────────────────────────────────────────────────────
    private Referrer AddReferrer(string name, Guid? mergedInto = null)
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = name, MergedIntoId = mergedInto };
        Context.Referrers.Add(r);
        return r;
    }

    private (Appointment Appt, Patient Patient) AddVisit(string referredBy, Guid? patientReferrerId, DateTime? whenUtc = null, string status = "CONFIRMED")
    {
        var patient = new Patient { PatientId = Guid.NewGuid(), HospitalId = HospitalId, FullName = "Pat " + Guid.NewGuid().ToString()[..4], ReferrerId = patientReferrerId };
        Context.Patients.Add(patient);
        var appt = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = whenUtc ?? DateTime.UtcNow, Status = status, Priority = "ROUTINE", ReferredBy = referredBy,
        };
        Context.Appointments.Add(appt);
        return (appt, patient);
    }

    private ReferralCommission AddCommission(Referrer r, Appointment? a, decimal amount, string status = "UNPAID", DateTime? serviceDate = null, string modality = "CT", Guid? serviceId = null, string? reference = null)
    {
        var c = new ReferralCommission
        {
            Id = Guid.NewGuid(), HospitalId = HospitalId, ReferrerId = r.ReferrerId, ReferrerName = r.Name ?? "",
            AppointmentId = a?.AppointmentId, AppointmentServiceId = serviceId, Modality = modality, CommissionAmount = amount, Status = status,
            TransactionDate = DateTime.UtcNow, ServiceDate = serviceDate ?? a?.DateTime ?? DateTime.UtcNow, ReferenceNumber = reference,
        };
        Context.ReferralCommissions.Add(c);
        return c;
    }

    private Invoice AddInvoice(Appointment a, decimal total, decimal paid, string status = "PENDING")
    {
        var inv = new Invoice
        {
            Id = Guid.NewGuid(), InvoiceId = "INV-" + Guid.NewGuid().ToString()[..6], HospitalId = HospitalId, AppointmentId = a.AppointmentId,
            PatientId = a.PatientId, GrossAmount = total, TotalAmount = total, PaidAmount = paid, Status = status,
        };
        Context.Invoices.Add(inv);
        return inv;
    }

    private GetReferralIntelligenceQueryHandler IntelligenceHandler() => new(Context, MockUserContext.Object);

    // ── intelligence: attribution ───────────────────────────────────────────────

    [Fact]
    public async Task Intelligence_AttributesEachVisitToItsOwnReferrer_NotThePatientsCurrentOne()
    {
        // Patient was first referred by Dr A; a later visit was re-pointed to Dr B
        // (ChangeReferrer also moves patient.ReferrerId to B). The EARLIER visit must
        // stay with A — it used to jump to B along with the patient link.
        var a = AddReferrer("DR A");
        var b = AddReferrer("DR B");
        var (oldVisit, patient) = AddVisit("DR A", b.ReferrerId);   // patient link already moved to B
        var newVisit = new Appointment
        {
            AppointmentId = Guid.NewGuid(), HospitalId = HospitalId, PatientId = patient.PatientId, PatientName = patient.FullName,
            DateTime = DateTime.UtcNow, Status = "CONFIRMED", Priority = "ROUTINE", ReferredBy = "DR B",
        };
        Context.Appointments.Add(newVisit);
        await Context.SaveChangesAsync();

        var result = await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(), CancellationToken.None);

        var nodeA = Assert.Single(result, r => r.Name == "DR A");
        var nodeB = Assert.Single(result, r => r.Name == "DR B");
        Assert.Equal(oldVisit.AppointmentId, Assert.Single(nodeA.Patients).AppointmentId);
        Assert.Equal(newVisit.AppointmentId, Assert.Single(nodeB.Patients).AppointmentId);
    }

    [Fact]
    public async Task Intelligence_ReassignedVisit_DoesNotShowOldReferrersPaidRowAsNewReferrersPaid()
    {
        // Visit was paid to Dr A (₹500 PAID), then re-assigned to Dr B: A keeps the PAID row
        // plus a −500 reversal, B gets a fresh 500 UNPAID row. B must show 0 paid / 500 unpaid.
        var a = AddReferrer("DR A");
        var b = AddReferrer("DR B");
        var (visit, _) = AddVisit("DR B", b.ReferrerId);
        AddCommission(a, visit, 500m, "PAID");
        AddCommission(a, visit, -500m, "UNPAID");
        AddCommission(b, visit, 500m, "UNPAID");
        await Context.SaveChangesAsync();

        var result = await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(), CancellationToken.None);

        var nodeB = Assert.Single(result, r => r.Name == "DR B");
        Assert.Equal(500m, nodeB.TotalCommission);
        Assert.Equal(0m, nodeB.PaidCommission);
        Assert.Equal(500m, nodeB.UnpaidCommission);
        var row = Assert.Single(nodeB.Patients);
        Assert.Equal(500m, row.CommissionAmount);          // only B's own row
        Assert.Equal("Unpaid", row.CommissionStatus);

        // A's money nets to zero (500 paid, −500 reversal) and is still reported.
        var nodeA = Assert.Single(result, r => r.Name == "DR A");
        Assert.Equal(0m, nodeA.TotalCommission);
        Assert.Equal(500m, nodeA.PaidCommission);
    }

    [Fact]
    public async Task Intelligence_VisitWithNoCommission_IsNotLabelledPaid()
    {
        var a = AddReferrer("DR A");
        AddVisit("DR A", a.ReferrerId);
        await Context.SaveChangesAsync();

        var result = await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(), CancellationToken.None);

        Assert.Equal("None", Assert.Single(Assert.Single(result).Patients).CommissionStatus);
    }

    [Fact]
    public async Task Intelligence_MixedPaidAndUnpaidLines_ReportsOnlyTheUnpaidPartAsUnpaidAmount()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        AddCommission(a, visit, 300m, "PAID", modality: "CT");
        AddCommission(a, visit, 200m, "UNPAID", modality: "USG");
        await Context.SaveChangesAsync();

        var row = Assert.Single(Assert.Single(await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(), CancellationToken.None)).Patients);

        Assert.Equal("Unpaid", row.CommissionStatus);
        Assert.Equal(500m, row.CommissionAmount);
        Assert.Equal(200m, row.UnpaidAmount);
    }

    [Fact]
    public async Task Intelligence_FilteringByAMergedPrimary_IncludesTheDuplicatesVisitsAndMoney()
    {
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE", mergedInto: primary.ReferrerId);
        var (v1, _) = AddVisit("DR PRIMARY", primary.ReferrerId);
        var (v2, _) = AddVisit("DR DUPE", dupe.ReferrerId);
        AddCommission(primary, v1, 100m);
        AddCommission(dupe, v2, 150m);
        await Context.SaveChangesAsync();

        var result = await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(ReferrerId: primary.ReferrerId), CancellationToken.None);

        var node = Assert.Single(result);
        Assert.Equal("DR PRIMARY", node.Name);
        Assert.Equal(2, node.TotalPatients);
        Assert.Equal(250m, node.TotalCommission);
    }

    [Fact]
    public async Task Intelligence_EndDateIsInclusiveInIst()
    {
        // 23:30 IST on the end date = 18:00 UTC same day → must be inside a bare-date range.
        // 00:30 IST the NEXT day = 19:00 UTC → outside it.
        var day = new DateTime(2026, 6, 15);
        var a = AddReferrer("DR A");
        var (inside, _) = AddVisit("DR A", a.ReferrerId, day.AddHours(18));
        AddVisit("DR A", a.ReferrerId, day.AddHours(19));
        await Context.SaveChangesAsync();

        var result = await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(day, day), CancellationToken.None);

        Assert.Equal(inside.AppointmentId, Assert.Single(Assert.Single(result).Patients).AppointmentId);
    }

    [Fact]
    public async Task Intelligence_ExcludesCancelledCommissionRowsFromTotals()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        AddCommission(a, visit, 100m, "UNPAID");
        AddCommission(a, null, 80m, "Cancelled");   // manually cancelled, amount retained
        await Context.SaveChangesAsync();

        var node = Assert.Single(await IntelligenceHandler().Handle(new GetReferralIntelligenceQuery(), CancellationToken.None));

        Assert.Equal(100m, node.TotalCommission);
    }

    // ── payout: gating + atomic batch ───────────────────────────────────────────

    [Fact]
    public async Task UpdateStatus_RefusesToPayWhenThePatientHasNotPaid()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        AddInvoice(visit, 1000m, 0m);
        var c = AddCommission(a, visit, 200m);
        await Context.SaveChangesAsync();

        var handler = new UpdateReferralCommissionStatusCommandHandler(Context);
        await Assert.ThrowsAsync<BusinessRuleViolationException>(() =>
            handler.Handle(new UpdateReferralCommissionStatusCommand(c.Id, "PAID"), CancellationToken.None));
    }

    [Fact]
    public async Task UpdateStatus_AllowsPaymentOncePatientHasPaidAnything()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        AddInvoice(visit, 1000m, 300m, "PARTIAL");
        var c = AddCommission(a, visit, 200m);
        await Context.SaveChangesAsync();

        await new UpdateReferralCommissionStatusCommandHandler(Context)
            .Handle(new UpdateReferralCommissionStatusCommand(c.Id, "PAID"), CancellationToken.None);

        var saved = Context.ReferralCommissions.Single(x => x.Id == c.Id);
        Assert.Equal("PAID", saved.Status);
        Assert.NotNull(saved.PaymentDate);
    }

    [Fact]
    public async Task UpdateStatus_ManualCommissionWithNoAppointmentIsNotGatedOnAPatientBill()
    {
        var a = AddReferrer("DR A");
        var c = AddCommission(a, null, 200m, reference: "MANUAL-1");
        await Context.SaveChangesAsync();

        await new UpdateReferralCommissionStatusCommandHandler(Context)
            .Handle(new UpdateReferralCommissionStatusCommand(c.Id, "PAID"), CancellationToken.None);

        Assert.Equal("PAID", Context.ReferralCommissions.Single(x => x.Id == c.Id).Status);
    }

    [Fact]
    public async Task BatchPay_PaysEligibleRowsSkipsTheRestAndIsSafeToRepeat()
    {
        var a = AddReferrer("DR A");
        var (paidVisit, _) = AddVisit("DR A", a.ReferrerId);
        AddInvoice(paidVisit, 1000m, 1000m, "PAID");
        var (unpaidVisit, _) = AddVisit("DR A", a.ReferrerId);
        AddInvoice(unpaidVisit, 1000m, 0m);
        var ok = AddCommission(a, paidVisit, 200m);
        var blocked = AddCommission(a, unpaidVisit, 300m);
        var deficit = AddCommission(a, null, -50m);
        await Context.SaveChangesAsync();

        var handler = new PayReferralCommissionsCommandHandler(Context);
        var cmd = new PayReferralCommissionsCommand(new() { ok.Id, blocked.Id, deficit.Id }, "Accountant Asha", "Dr A", "9999999999");
        var first = await handler.Handle(cmd, CancellationToken.None);

        Assert.Equal(new[] { ok.Id }, first.Paid);
        Assert.Equal(200m, first.TotalPaid);
        Assert.Equal(2, first.Skipped.Count);
        var row = Context.ReferralCommissions.Single(x => x.Id == ok.Id);
        Assert.Equal("PAID", row.Status);
        Assert.Equal("Accountant Asha", row.PaidBy);
        Assert.Equal("Dr A", row.PayeeName);
        Assert.NotNull(row.PaymentDate);
        Assert.Equal("UNPAID", Context.ReferralCommissions.Single(x => x.Id == blocked.Id).Status);

        // Re-submitting the same payout is idempotent: nothing new is paid, no exception.
        var second = await handler.Handle(cmd, CancellationToken.None);
        Assert.Empty(second.Paid);
        Assert.Contains(second.Skipped, s => s.CommissionId == ok.Id && s.Reason.Contains("Already paid"));
    }

    [Fact]
    public async Task BatchPay_RequiresPaidByAndPayeeName()
    {
        var a = AddReferrer("DR A");
        var c = AddCommission(a, null, 100m);
        await Context.SaveChangesAsync();
        var handler = new PayReferralCommissionsCommandHandler(Context);

        await Assert.ThrowsAsync<ValidationException>(() =>
            handler.Handle(new PayReferralCommissionsCommand(new() { c.Id }, " ", "Dr A"), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            handler.Handle(new PayReferralCommissionsCommand(new() { c.Id }, "Asha", ""), CancellationToken.None));
    }

    // ── batch record: status, matching, cap ─────────────────────────────────────

    [Fact]
    public async Task RecordBatch_RejectsUnknownStatusAndStampsPaymentDateOnPaid()
    {
        var a = AddReferrer("DR A");
        await Context.SaveChangesAsync();
        var handler = new RecordReferralCommissionsCommandHandler(Context);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(
            new RecordReferralCommissionsCommand(a.ReferrerId, null, null, null, null, new() { new CommissionLine("MRI", 10m, "bogus") }),
            CancellationToken.None));

        // A write-off line is booked directly as PAID and must carry a payment date.
        var ids = await handler.Handle(
            new RecordReferralCommissionsCommand(a.ReferrerId, null, "write-off", null, null, new() { new CommissionLine("WRITE-OFF", 100m, "paid") }),
            CancellationToken.None);
        var row = Context.ReferralCommissions.Single(x => x.Id == ids[0]);
        Assert.Equal("PAID", row.Status);
        Assert.NotNull(row.PaymentDate);
    }

    [Fact]
    public async Task RecordBatch_MatchesExistingRowsByServiceLine_NotByListOrder()
    {
        // Two CT services on one invoice. Re-saving with the lines in swapped order must
        // update each row in place (by service id), not cross-assign the amounts.
        var a = AddReferrer("DR A");
        var svc1 = Guid.NewGuid();
        var svc2 = Guid.NewGuid();
        var r1 = AddCommission(a, null, 100m, modality: "CT", serviceId: svc1, reference: "INV-X");
        var r2 = AddCommission(a, null, 200m, modality: "CT", serviceId: svc2, reference: "INV-X");
        await Context.SaveChangesAsync();

        await new RecordReferralCommissionsCommandHandler(Context).Handle(
            new RecordReferralCommissionsCommand(a.ReferrerId, "INV-X", null, null, null, new()
            {
                new CommissionLine("CT", 250m, "UNPAID", svc2),   // second service first
                new CommissionLine("CT", 120m, "UNPAID", svc1),
            }), CancellationToken.None);

        Assert.Equal(120m, Context.ReferralCommissions.Single(x => x.Id == r1.Id).CommissionAmount);
        Assert.Equal(250m, Context.ReferralCommissions.Single(x => x.Id == r2.Id).CommissionAmount);
    }

    [Fact]
    public async Task RecordBatch_RejectsACommissionAboveItsServiceAmount()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        var svc = new AppointmentService { AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "USG", Modality = "USG", Amount = 500m };
        Context.AppointmentServices.Add(svc);
        await Context.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => new RecordReferralCommissionsCommandHandler(Context).Handle(
            new RecordReferralCommissionsCommand(a.ReferrerId, "INV-1", null, null, visit.AppointmentId,
                new() { new CommissionLine("USG", 600m, "UNPAID", svc.Id) }), CancellationToken.None));
    }

    // ── accumulated total ───────────────────────────────────────────────────────

    [Fact]
    public async Task AccumulatedTotal_SkipsCancelledRows()
    {
        var a = AddReferrer("DR A");
        var first = AddCommission(a, null, 100m);
        first.TransactionDate = DateTime.UtcNow.AddMinutes(-3);
        var cancelled = AddCommission(a, null, 80m, "Cancelled");
        cancelled.TransactionDate = DateTime.UtcNow.AddMinutes(-2);
        var last = AddCommission(a, null, 50m);
        last.TransactionDate = DateTime.UtcNow.AddMinutes(-1);
        await Context.SaveChangesAsync();

        await ReferralLedger.RecomputeAccumulatedTotal(Context, a.ReferrerId, HospitalId, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(100m, Context.ReferralCommissions.Single(x => x.Id == first.Id).AccumulatedTotal);
        Assert.Equal(150m, Context.ReferralCommissions.Single(x => x.Id == last.Id).AccumulatedTotal);
    }

    // ── merge / delete safety ───────────────────────────────────────────────────

    [Fact]
    public async Task Merge_RefusesALoop()
    {
        var a = AddReferrer("DR A");
        var b = AddReferrer("DR B", mergedInto: a.ReferrerId);   // B is already an alias of A
        await Context.SaveChangesAsync();

        // Merging A into B would close A → B → A.
        await Assert.ThrowsAsync<ValidationException>(() =>
            new MergeReferrersCommandHandler(Context).Handle(new MergeReferrersCommand(a.ReferrerId, b.ReferrerId), CancellationToken.None));
    }

    [Fact]
    public async Task Merge_PointsAtTheTargetsRootInsteadOfBuildingAChain()
    {
        var root = AddReferrer("DR ROOT");
        var mid = AddReferrer("DR MID", mergedInto: root.ReferrerId);
        var dupe = AddReferrer("DR DUPE");
        await Context.SaveChangesAsync();

        await new MergeReferrersCommandHandler(Context).Handle(new MergeReferrersCommand(dupe.ReferrerId, mid.ReferrerId), CancellationToken.None);

        Assert.Equal(root.ReferrerId, Context.Referrers.Single(r => r.ReferrerId == dupe.ReferrerId).MergedIntoId);
    }

    [Fact]
    public async Task Merge_RefusesToTouchSelfOrAnAlreadyMergedSource()
    {
        var self = AddReferrer("Self");
        var a = AddReferrer("DR A");
        var root = AddReferrer("DR ROOT");
        var alias = AddReferrer("DR ALIAS", mergedInto: root.ReferrerId);
        await Context.SaveChangesAsync();
        var handler = new MergeReferrersCommandHandler(Context);

        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new MergeReferrersCommand(a.ReferrerId, self.ReferrerId), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new MergeReferrersCommand(alias.ReferrerId, a.ReferrerId), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_IsBlockedWhileUnpaidOrClawbackBalanceRemains_ButNotForAPaidUpPartner()
    {
        var owed = AddReferrer("DR OWED");
        AddCommission(owed, null, 100m, "UNPAID");
        var settled = AddReferrer("DR SETTLED");
        AddCommission(settled, null, 100m, "PAID");
        await Context.SaveChangesAsync();
        var handler = new DeleteReferrerCommandHandler(Context);

        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new DeleteReferrerCommand(owed.ReferrerId), CancellationToken.None));
        Assert.True(await handler.Handle(new DeleteReferrerCommand(settled.ReferrerId), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_AMergedAliasIsAlwaysAllowed_AndARootWithAliasesIsNot()
    {
        var root = AddReferrer("DR ROOT");
        var alias = AddReferrer("DR ALIAS", mergedInto: root.ReferrerId);
        AddCommission(alias, null, 100m, "UNPAID");   // its money is carried by the root
        await Context.SaveChangesAsync();
        var handler = new DeleteReferrerCommandHandler(Context);

        await Assert.ThrowsAsync<BusinessRuleViolationException>(() => handler.Handle(new DeleteReferrerCommand(root.ReferrerId), CancellationToken.None));
        Assert.True(await handler.Handle(new DeleteReferrerCommand(alias.ReferrerId), CancellationToken.None));
    }

    // ── re-assigning a visit ────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeReferrer_ToTheSameReferrer_DoesNotRewriteAPaidOrAdjustedAmount()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        var svc = new AppointmentService { AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "CT", Modality = "CT", Amount = 1000m, ReferralCutValue = 500m };
        Context.AppointmentServices.Add(svc);
        // Concession already took the cut from 500 down to 300 and it has been paid.
        var c = AddCommission(a, visit, 300m, "PAID", serviceId: svc.Id);
        await Context.SaveChangesAsync();

        await new ChangeReferrerCommandHandler(Context).Handle(
            new ChangeReferrerCommand { AppointmentId = visit.AppointmentId, NewReferrerName = "DR A" }, CancellationToken.None);

        var saved = Context.ReferralCommissions.Single(x => x.Id == c.Id);
        Assert.Equal(300m, saved.CommissionAmount);
        Assert.Equal("PAID", saved.Status);
        Assert.Equal(1, Context.ReferralCommissions.Count(x => x.AppointmentId == visit.AppointmentId));
    }

    [Fact]
    public async Task ChangeReferrer_MovesAnUnpaidRowKeepingItsConcessionAdjustedAmount()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        var svc = new AppointmentService { AppointmentId = visit.AppointmentId, HospitalId = HospitalId, ServiceName = "CT", Modality = "CT", Amount = 1000m, ReferralCutValue = 500m };
        Context.AppointmentServices.Add(svc);
        var c = AddCommission(a, visit, 300m, "UNPAID", serviceId: svc.Id);   // 500 cut less 200 concession
        await Context.SaveChangesAsync();

        await new ChangeReferrerCommandHandler(Context).Handle(
            new ChangeReferrerCommand { AppointmentId = visit.AppointmentId, NewReferrerName = "DR NEW" }, CancellationToken.None);

        var moved = Context.ReferralCommissions.Single(x => x.Id == c.Id);
        Assert.Equal(300m, moved.CommissionAmount);     // not reset to the 500 base cut
        Assert.Equal("DR NEW", moved.ReferrerName);
    }

    [Fact]
    public async Task ChangeReferrer_LeavesAClawbackDeficitWithTheReferrerWhoOwesIt()
    {
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        var deficit = AddCommission(a, visit, -300m, "UNPAID");
        await Context.SaveChangesAsync();

        await new ChangeReferrerCommandHandler(Context).Handle(
            new ChangeReferrerCommand { AppointmentId = visit.AppointmentId, NewReferrerName = "DR NEW" }, CancellationToken.None);

        var saved = Context.ReferralCommissions.Single(x => x.Id == deficit.Id);
        Assert.Equal(a.ReferrerId, saved.ReferrerId);
        Assert.Equal(-300m, saved.CommissionAmount);
    }

    // ── doctor portal ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Portal_CombinesMergedDuplicatesAndSplitsPayableFromAwaiting()
    {
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE", mergedInto: primary.ReferrerId);
        var (paidVisit, _) = AddVisit("DR PRIMARY", primary.ReferrerId);
        AddInvoice(paidVisit, 1000m, 1000m, "PAID");
        var (waitingVisit, _) = AddVisit("DR DUPE", dupe.ReferrerId);
        AddInvoice(waitingVisit, 1000m, 0m);
        AddCommission(primary, paidVisit, 200m);
        AddCommission(dupe, waitingVisit, 300m);
        AddCommission(primary, null, 999m, "Cancelled");
        await Context.SaveChangesAsync();

        // The link belongs to the DUPLICATE — the doctor still sees the combined picture.
        var portal = await new GetDoctorPortalQueryHandler(Context).Handle(new GetDoctorPortalQuery(dupe.ReferrerId), CancellationToken.None);

        Assert.NotNull(portal);
        Assert.Equal("DR PRIMARY", portal!.DoctorName);
        Assert.Equal(500m, portal.TotalEligible);
        Assert.Equal(200m, portal.PayableNow);
        Assert.Equal(300m, portal.AwaitingPatient);
    }

    // ── commission list / ledger / matrix ───────────────────────────────────────

    [Fact]
    public async Task CommissionList_IgnoresASoftDeletedInvoiceWhenResolvingPatientPayment()
    {
        // The only invoice that shows money is soft-deleted; the live one is unpaid.
        // The row must read PENDING (not payable), not PAID via the deleted bill.
        var a = AddReferrer("DR A");
        var (visit, _) = AddVisit("DR A", a.ReferrerId);
        var ghost = AddInvoice(visit, 1000m, 1000m, "PAID");
        ghost.DeletedAt = DateTime.UtcNow;
        AddInvoice(visit, 1000m, 0m);
        AddCommission(a, visit, 200m);
        await Context.SaveChangesAsync();

        var rows = await new GetReferralCommissionsQueryHandler(Context).Handle(new GetReferralCommissionsQuery(), CancellationToken.None);

        Assert.Equal("PENDING", Assert.Single(rows).PatientPaymentStatus);
    }

    [Fact]
    public async Task Ledger_EndDateCoversTheWholeIstDayAndBucketsByServiceDate()
    {
        var day = new DateTime(2026, 6, 15);
        var a = AddReferrer("DR A");
        var inside = AddCommission(a, null, 100m, serviceDate: day.AddHours(18));      // 23:30 IST on the end date
        var outside = AddCommission(a, null, 50m, serviceDate: day.AddHours(19));      // 00:30 IST next day
        inside.TransactionDate = day.AddDays(10);                                      // recorded long after the visit
        await Context.SaveChangesAsync();

        var rows = await new GetDetailedReferralLedgerQueryHandler(Context)
            .Handle(new GetDetailedReferralLedgerQuery(day, day), CancellationToken.None);

        Assert.Equal(inside.Id, Assert.Single(rows).CommissionId);
        Assert.DoesNotContain(rows, r => r.CommissionId == outside.Id);
    }

    [Fact]
    public async Task Matrix_BucketsByIstHourAndRollsMergedDuplicatesIntoThePrimary()
    {
        // 12:30 UTC = 18:00 IST → Evening. The UTC hour (12) would have said Afternoon.
        var day = new DateTime(2026, 6, 15);
        var primary = AddReferrer("DR PRIMARY");
        var dupe = AddReferrer("DR DUPE", mergedInto: primary.ReferrerId);
        AddVisit("DR PRIMARY", primary.ReferrerId, day.AddHours(12).AddMinutes(30));
        AddVisit("DR DUPE", dupe.ReferrerId, day.AddHours(12).AddMinutes(30));
        await Context.SaveChangesAsync();

        var matrix = await new GetReferralMatrixQueryHandler(Context)
            .Handle(new GetReferralMatrixQuery("DAY", day, 1), CancellationToken.None);

        var row = Assert.Single(matrix.Rows);
        Assert.Equal("DR PRIMARY", row.Name);
        Assert.Equal(2, row.Total);
        Assert.Equal(2, row.Counts["Evening (5pm-12am)"]);
        Assert.Equal(0, row.Counts["Afternoon (12pm-5pm)"]);
    }
}
