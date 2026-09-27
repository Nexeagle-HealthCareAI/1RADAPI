using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using _1Rad.Application.Features.Referrers.Commands.PayReferralCommissions;
using _1Rad.Application.Interfaces;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using _1Rad.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace _1Rad.UnitTests.Features.Referrers;

/// <summary>
/// Two people paying the same partner at the same moment (two tabs, two accountants). Status is the
/// commission's concurrency token, so the loser's save is refused instead of paying the row twice or
/// netting the same deficit twice. Two contexts over ONE store stand in for the two requests; the
/// loser's context has already read the row while it was still unpaid.
/// </summary>
public class ReferralPayoutConcurrencyTests
{
    private readonly Guid _hospitalId = Guid.NewGuid();
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public ReferralPayoutConcurrencyTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    // Every request gets its own context (its own change tracker) over the shared store.
    private ApplicationDbContext NewRequestContext()
    {
        var user = new Mock<IUserContext>();
        user.Setup(x => x.HospitalId).Returns(_hospitalId);
        user.Setup(x => x.UserId).Returns(Guid.NewGuid());
        return new ApplicationDbContext(_options, new Mock<IPublisher>().Object, user.Object);
    }

    private async Task<Guid> SeedUnpaidCommission()
    {
        await using var seed = NewRequestContext();
        var referrer = new Referrer { ReferrerId = Guid.NewGuid(), HospitalId = _hospitalId, Name = "DR A", Contact = "9876500000" };
        var commission = new ReferralCommission
        {
            Id = Guid.NewGuid(), HospitalId = _hospitalId, ReferrerId = referrer.ReferrerId, ReferrerName = referrer.Name,
            Modality = "CT", CommissionAmount = 500m, Status = "UNPAID",
        };
        seed.Referrers.Add(referrer);
        seed.ReferralCommissions.Add(commission);
        await seed.SaveChangesAsync();
        return commission.Id;
    }

    private static PayReferralCommissionsCommand PayCommand(Guid id) =>
        new(new List<Guid> { id }, "Accountant", "DR A");

    [Fact]
    public async Task ASecondPayoutThatReadTheRowBeforeTheFirstSaved_IsRefused_NotPaidTwice()
    {
        var id = await SeedUnpaidCommission();

        await using var firstRequest = NewRequestContext();
        await using var secondRequest = NewRequestContext();
        // The second request has already read the row - still unpaid - when the first one pays it.
        await secondRequest.ReferralCommissions.ToListAsync();

        var first = await new PayReferralCommissionsCommandHandler(firstRequest)
            .Handle(PayCommand(id), CancellationToken.None);
        Assert.Equal(new[] { id }, first.Paid);

        await Assert.ThrowsAsync<ConflictException>(() =>
            new PayReferralCommissionsCommandHandler(secondRequest).Handle(PayCommand(id), CancellationToken.None));

        // Paid exactly once: the row carries the first payout's stamp.
        await using var check = NewRequestContext();
        var row = await check.ReferralCommissions.SingleAsync(c => c.Id == id);
        Assert.Equal("PAID", row.Status, ignoreCase: true);
    }

    [Fact]
    public async Task ARepeatPayoutAfterTheFirstFinished_StillJustSkipsTheRow()
    {
        var id = await SeedUnpaidCommission();

        await using var firstRequest = NewRequestContext();
        await new PayReferralCommissionsCommandHandler(firstRequest).Handle(PayCommand(id), CancellationToken.None);

        // A later, non-overlapping request reads the row fresh: idempotent, not an error.
        await using var laterRequest = NewRequestContext();
        var later = await new PayReferralCommissionsCommandHandler(laterRequest)
            .Handle(PayCommand(id), CancellationToken.None);

        Assert.Empty(later.Paid);
        Assert.Contains(later.Skipped, s => s.CommissionId == id && s.Reason == "Already paid.");
    }
}
