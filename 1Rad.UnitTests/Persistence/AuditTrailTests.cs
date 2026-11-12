using _1Rad.Domain.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace _1Rad.UnitTests.Persistence;

// Gap analysis P3-8: changes to audited records write an AuditLog row in the same save.
public class AuditTrailTests : BaseHandlerTest
{
    [Fact]
    public async Task Add_modify_and_soft_signals_are_recorded_with_user_and_centre()
    {
        var charge = new ServiceCharge { HospitalId = HospitalId, Modality = "CT", ServiceName = "CT Brain", Amount = 2500 };
        Context.ServiceCharges.Add(charge);
        await Context.SaveChangesAsync();

        charge.Amount = 2800;
        await Context.SaveChangesAsync();

        var logs = await Context.AuditLogs.IgnoreQueryFilters()
            .Where(a => a.EntityType == nameof(ServiceCharge))
            .OrderBy(a => a.Id).ToListAsync();

        logs.Should().HaveCount(2);
        logs[0].Action.Should().Be("Added");
        logs[0].EntityId.Should().Be(charge.Id.ToString());
        logs[0].HospitalId.Should().Be(HospitalId);
        logs[0].UserId.Should().Be(UserId);
        logs[1].Action.Should().Be("Modified");
        logs[1].ChangesJson.Should().Contain("\"Amount\"").And.Contain("2500").And.Contain("2800");
    }

    [Fact]
    public async Task Passwords_are_never_written()
    {
        var user = new User { FullName = "Test", Email = "t@example.com", Mobile = "9999999999", Password = "plain-secret", PasswordHash = "hash-secret" };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();

        var log = await Context.AuditLogs.IgnoreQueryFilters().SingleAsync(a => a.EntityType == nameof(User));
        log.ChangesJson.Should().NotContain("plain-secret").And.NotContain("hash-secret");
    }

    [Fact]
    public async Task Unaudited_entities_write_nothing()
    {
        Context.RadAiQuestionLogs.Add(new RadAiQuestionLog { HospitalId = HospitalId, Question = "q" });
        await Context.SaveChangesAsync();
        (await Context.AuditLogs.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }
}
