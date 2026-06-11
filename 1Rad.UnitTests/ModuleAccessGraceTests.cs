using _1Rad.Application.Interfaces;
using _1Rad.Domain.Constants;
using _1Rad.Domain.Entities;
using _1Rad.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace _1Rad.UnitTests;

/// <summary>
/// PACS downgrade grace window: a removed-but-within-grace center keeps
/// read-only (GraceRead) access to its studies; after the window it's None.
/// </summary>
public class ModuleAccessGraceTests : BaseHandlerTest
{
    private ModuleEntitlementService Service(int graceDays = 30)
    {
        var cfg = new ConfigurationManager();
        cfg["Pacs:GraceDays"] = graceDays.ToString();
        return new ModuleEntitlementService(Context, new MemoryCache(new MemoryCacheOptions()), cfg);
    }

    private async Task SeedSubAsync(string modules, DateTime? pacsRemovedAt)
    {
        Context.HospitalSubscriptions.Add(new HospitalSubscription
        {
            SubscriptionId = Guid.NewGuid(),
            HospitalId = HospitalId,
            Modules = modules,
            PacsRemovedAt = pacsRemovedAt,
            CreatedAt = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddYears(1),
        });
        await Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Pacs_present_is_Full()
    {
        await SeedSubAsync("RIS,PACS", null);
        (await Service().GetModuleAccessAsync(HospitalId, ModuleConstants.Pacs, default))
            .Should().Be(ModuleAccess.Full);
    }

    [Fact]
    public async Task Pacs_removed_within_grace_is_GraceRead()
    {
        await SeedSubAsync("RIS", pacsRemovedAt: DateTime.UtcNow.AddDays(-5));
        (await Service(graceDays: 30).GetModuleAccessAsync(HospitalId, ModuleConstants.Pacs, default))
            .Should().Be(ModuleAccess.GraceRead);
    }

    [Fact]
    public async Task Pacs_removed_after_grace_is_None()
    {
        await SeedSubAsync("RIS", pacsRemovedAt: DateTime.UtcNow.AddDays(-40));
        (await Service(graceDays: 30).GetModuleAccessAsync(HospitalId, ModuleConstants.Pacs, default))
            .Should().Be(ModuleAccess.None);
    }

    [Fact]
    public async Task Never_had_pacs_is_None()
    {
        await SeedSubAsync("RIS", null);
        (await Service().GetModuleAccessAsync(HospitalId, ModuleConstants.Pacs, default))
            .Should().Be(ModuleAccess.None);
    }

    [Fact]
    public async Task Ris_is_Full_when_present()
    {
        await SeedSubAsync("RIS", pacsRemovedAt: DateTime.UtcNow.AddDays(-5));
        (await Service().GetModuleAccessAsync(HospitalId, ModuleConstants.Ris, default))
            .Should().Be(ModuleAccess.Full);
    }
}
