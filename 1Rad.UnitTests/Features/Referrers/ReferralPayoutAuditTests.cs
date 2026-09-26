using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Referrers.Commands.PayReferralCommissions;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;
using _1Rad.Application.Features.Referrers.Commands.WriteOffReferralDeficit;
using _1Rad.Domain.Entities;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// PaidBy / PayeeName are what was typed on the payout form (who handed over the money, who received
/// it). The audit question "which login did this" is answered by UpdatedBy, and that comes from the
/// signed-in account - never from the request.
/// </summary>
public class ReferralPayoutAuditTests : BaseHandlerTest
{
    private string ExpectedActor => $"Asha Accountant [{UserId}]";

    private Referrer AddReferrer()
    {
        var r = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = "DR A", Contact = "9876500000" };
        Context.Referrers.Add(r);
        return r;
    }

    private ReferralCommission AddCommission(Referrer r, decimal amount, string status = "UNPAID")
    {
        var c = new ReferralCommission
        {
            Id = Guid.NewGuid(), HospitalId = HospitalId, ReferrerId = r.ReferrerId, ReferrerName = r.Name,
            Modality = "CT", CommissionAmount = amount, Status = status,
        };
        Context.ReferralCommissions.Add(c);
        return c;
    }

    private void AddSignedInUser(string fullName = "Asha Accountant") =>
        Context.Users.Add(new User { UserId = UserId, FullName = fullName, Email = "asha@example.com", Mobile = "9000000000", PasswordHash = "h" });

    [Fact]
    public async Task APayout_RecordsTheSignedInAccount_AndKeepsTheTypedPaidBy()
    {
        var dr = AddReferrer();
        var commission = AddCommission(dr, 500m);
        AddSignedInUser();
        await Context.SaveChangesAsync();

        await new PayReferralCommissionsCommandHandler(Context).Handle(
            new PayReferralCommissionsCommand(new List<Guid> { commission.Id }, "Ravi (front desk)", "DR A"),
            CancellationToken.None);

        var saved = Context.ReferralCommissions.Single(c => c.Id == commission.Id);
        Assert.Equal("Ravi (front desk)", saved.PaidBy);   // the person who handed over the cash, as typed
        Assert.Equal(ExpectedActor, saved.UpdatedBy);      // the account that recorded it
    }

    [Fact]
    public async Task ADeficitNettedOutOfAPayout_IsAlsoStampedWithTheSignedInAccount()
    {
        var dr = AddReferrer();
        var commission = AddCommission(dr, 500m);
        var deficit = AddCommission(dr, -200m);
        AddSignedInUser();
        await Context.SaveChangesAsync();

        await new PayReferralCommissionsCommandHandler(Context).Handle(
            new PayReferralCommissionsCommand(new List<Guid> { commission.Id }, "Ravi", "DR A", NetDeficits: true),
            CancellationToken.None);

        Assert.Equal(ExpectedActor, Context.ReferralCommissions.Single(c => c.Id == deficit.Id).UpdatedBy);
    }

    [Fact]
    public async Task AClientSuppliedUpdatedBy_IsIgnoredOnAStatusChange()
    {
        var dr = AddReferrer();
        var commission = AddCommission(dr, 500m);
        AddSignedInUser();
        await Context.SaveChangesAsync();

        await new UpdateReferralCommissionStatusCommandHandler(Context).Handle(
            new UpdateReferralCommissionStatusCommand(commission.Id, "PAID", PaidBy: "Ravi", PayeeName: "DR A", UpdatedBy: "someone else entirely"),
            CancellationToken.None);

        Assert.Equal(ExpectedActor, Context.ReferralCommissions.Single(c => c.Id == commission.Id).UpdatedBy);
    }

    [Fact]
    public async Task AWriteOff_IsStampedWithTheSignedInAccount()
    {
        var dr = AddReferrer();
        var deficit = AddCommission(dr, -300m);
        AddSignedInUser();
        await Context.SaveChangesAsync();

        await new WriteOffReferralDeficitCommandHandler(Context).Handle(
            new WriteOffReferralDeficitCommand(dr.ReferrerId), CancellationToken.None);

        Assert.Equal(ExpectedActor, Context.ReferralCommissions.Single(c => c.Id == deficit.Id).UpdatedBy);
        var writeOffRow = Context.ReferralCommissions.Single(c => c.Modality == "WRITE-OFF");
        Assert.Equal(ExpectedActor, writeOffRow.CreatedBy);
    }

    [Theory]
    [InlineData("PaidBy", 201)]
    [InlineData("PayeeName", 201)]
    [InlineData("PayeeContact", 41)]
    [InlineData("PayeeEmail", 201)]
    [InlineData("PayeeAddress", 501)]
    public async Task AnOverLongPayoutField_IsRefusedWithAMessage_AndNothingIsPaid(string field, int length)
    {
        var dr = AddReferrer();
        var commission = AddCommission(dr, 500m);
        await Context.SaveChangesAsync();

        var tooLong = new string('x', length);
        var command = new PayReferralCommissionsCommand(
            new List<Guid> { commission.Id },
            field == "PaidBy" ? tooLong : "Ravi",
            field == "PayeeName" ? tooLong : "DR A",
            field == "PayeeContact" ? tooLong : null,
            field == "PayeeEmail" ? tooLong : null,
            field == "PayeeAddress" ? tooLong : null);

        await Assert.ThrowsAsync<_1Rad.Domain.Exceptions.ValidationException>(() =>
            new PayReferralCommissionsCommandHandler(Context).Handle(command, CancellationToken.None));
        Assert.Equal("UNPAID", Context.ReferralCommissions.Single(c => c.Id == commission.Id).Status);
    }

    [Fact]
    public async Task AnAccountWithNoNameOnFile_StillLeavesATraceableActor()
    {
        var dr = AddReferrer();
        var commission = AddCommission(dr, 500m);
        await Context.SaveChangesAsync();   // no User row for the signed-in id

        await new UpdateReferralCommissionStatusCommandHandler(Context).Handle(
            new UpdateReferralCommissionStatusCommand(commission.Id, "PAID", PaidBy: "Ravi", PayeeName: "DR A"),
            CancellationToken.None);

        Assert.Equal($"User [{UserId}]", Context.ReferralCommissions.Single(c => c.Id == commission.Id).UpdatedBy);
    }
}
