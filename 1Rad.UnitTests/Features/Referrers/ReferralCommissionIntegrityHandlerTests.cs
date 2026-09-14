using System;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Approvals.Commands.ReviewApproval;
using _1Rad.Application.Features.Referrers.Commands.RecordReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommission;
using _1Rad.Application.Features.Referrers.Commands.UpdateReferralCommissionStatus;
using _1Rad.Domain.Entities;
using MediatR;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

public class ReferralCommissionIntegrityHandlerTests : BaseHandlerTest
{
    [Fact]
    public async Task RecordCommission_ExcludesSoftDeletedRowsFromRunningTotal()
    {
        var referrer = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = HospitalId, Name = "Dr. Referral" };
        Context.Referrers.Add(referrer);
        Context.ReferralCommissions.Add(new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = referrer.ReferrerId,
            ReferrerName = referrer.Name,
            HospitalId = HospitalId,
            CommissionAmount = 100m,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            DeletedAt = DateTime.UtcNow
        });
        await Context.SaveChangesAsync();

        var handler = new RecordReferralCommissionCommandHandler(Context);
        var commissionId = await handler.Handle(new RecordReferralCommissionCommand(
            referrer.ReferrerId, 20m, "USG", "MANUAL-001", null), CancellationToken.None);

        var commission = await Context.ReferralCommissions.FindAsync(commissionId);
        Assert.NotNull(commission);
        Assert.Equal(20m, commission.AccumulatedTotal);
    }

    [Fact]
    public async Task UpdateCommission_RejectsAppointmentLinkedCommission()
    {
        var commission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = Guid.NewGuid(),
            HospitalId = HospitalId,
            AppointmentId = Guid.NewGuid(),
            CommissionAmount = 100m,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow
        };
        Context.ReferralCommissions.Add(commission);
        await Context.SaveChangesAsync();

        var handler = new UpdateReferralCommissionCommandHandler(Context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new UpdateReferralCommissionCommand(commission.Id, 300m, "CT", null, null, "UNPAID"),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCommissionStatus_RejectsDirectUnpayOfPaidCommission()
    {
        var commission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = Guid.NewGuid(),
            HospitalId = HospitalId,
            CommissionAmount = 100m,
            Status = "PAID",
            TransactionDate = DateTime.UtcNow,
            PaymentDate = DateTime.UtcNow
        };
        Context.ReferralCommissions.Add(commission);
        await Context.SaveChangesAsync();

        var handler = new UpdateReferralCommissionStatusCommandHandler(Context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new UpdateReferralCommissionStatusCommand(commission.Id, "UNPAID"),
            CancellationToken.None));
    }

    [Fact]
    public async Task UpdateCommissionStatus_RejectsDirectCancellationOfAppointmentCommission()
    {
        var commission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = Guid.NewGuid(),
            HospitalId = HospitalId,
            AppointmentId = Guid.NewGuid(),
            CommissionAmount = 100m,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow
        };
        Context.ReferralCommissions.Add(commission);
        await Context.SaveChangesAsync();

        var handler = new UpdateReferralCommissionStatusCommandHandler(Context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new UpdateReferralCommissionStatusCommand(commission.Id, "CANCELLED"),
            CancellationToken.None));
    }

    [Fact]
    public async Task ApprovePayoutRevision_AllowsZeroAndRebasesRunningTotals()
    {
        var referrerId = Guid.NewGuid();
        var earlierCommission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = referrerId,
            HospitalId = HospitalId,
            CommissionAmount = 100m,
            Status = "UNPAID",
            TransactionDate = DateTime.UtcNow.AddDays(-1),
            AccumulatedTotal = 100m
        };
        var revisedCommission = new ReferralCommission
        {
            Id = Guid.NewGuid(),
            ReferrerId = referrerId,
            HospitalId = HospitalId,
            CommissionAmount = 75m,
            Status = "PAID",
            TransactionDate = DateTime.UtcNow,
            AccumulatedTotal = 175m
        };
        var approval = new ApprovalRequest
        {
            Id = Guid.NewGuid(),
            HospitalId = HospitalId,
            Type = "EDIT_COMMISSION",
            Status = "PENDING",
            Reason = "Corrected payout calculation",
            Payload = $"{{\"commissionId\":\"{revisedCommission.Id}\",\"amount\":0,\"status\":\"PAID\"}}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        Context.ReferralCommissions.AddRange(earlierCommission, revisedCommission);
        Context.ApprovalRequests.Add(approval);
        await Context.SaveChangesAsync();

        var handler = new ReviewApprovalCommandHandler(Context, new Mock<IMediator>().Object);
        var result = await handler.Handle(new ReviewApprovalCommand
        {
            Id = approval.Id,
            Approve = true,
            Note = "Approved after review"
        }, CancellationToken.None);

        Assert.True(result);
        Assert.Equal("APPROVED", approval.Status);
        Assert.Equal(0m, revisedCommission.CommissionAmount);
        Assert.Equal(100m, earlierCommission.AccumulatedTotal);
        Assert.Equal(100m, revisedCommission.AccumulatedTotal);
        Assert.Equal(UserId, approval.ReviewedBy);
    }
}