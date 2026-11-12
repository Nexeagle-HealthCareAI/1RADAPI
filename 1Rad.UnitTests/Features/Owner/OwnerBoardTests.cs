using System.Security.Claims;
using _1Rad.Application.Features.Owner;
using _1Rad.Domain.Entities;
using _1Rad.Domain.Exceptions;
using _1Rad.Infrastructure.Authentication;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace _1Rad.UnitTests.Features.Owner;

/// <summary>
/// Owner board (phase 4): who counts as an owner of a centre, copying one centre's
/// setup to another, linked centres following the main centre, and the
/// acting-centre override that lets the board read each centre as itself.
/// </summary>
public class OwnerBoardTests : BaseHandlerTest
{
    private readonly Guid _second = Guid.NewGuid();
    private readonly Guid _third = Guid.NewGuid();
    private readonly Guid _notMine = Guid.NewGuid();

    private async Task SeedAsync()
    {
        var admin = new Role { RoleId = 1, RoleName = "AdminDoctor" };
        var desk = new Role { RoleId = 4, RoleName = "Receptionist" };
        Context.Roles.AddRange(admin, desk);
        Context.Hospitals.AddRange(
            new Hospital { HospitalId = HospitalId, HospitalName = "Main Road", CreatedAt = DateTime.UtcNow.AddYears(-2), IsAutoBillingEnabled = true },
            new Hospital { HospitalId = _second, HospitalName = "Lake Town", CreatedAt = DateTime.UtcNow.AddYears(-1) },
            new Hospital { HospitalId = _third, HospitalName = "Salt Lake", CreatedAt = DateTime.UtcNow },
            new Hospital { HospitalId = _notMine, HospitalName = "Someone else", CreatedAt = DateTime.UtcNow });
        var m1 = new UserHospitalMapping { UserId = UserId, HospitalId = HospitalId, IsDefault = true }; m1.Roles.Add(admin);
        var m2 = new UserHospitalMapping { UserId = UserId, HospitalId = _second }; m2.Roles.Add(admin);
        var m3 = new UserHospitalMapping { UserId = UserId, HospitalId = _third }; m3.Roles.Add(admin);
        var m4 = new UserHospitalMapping { UserId = UserId, HospitalId = _notMine }; m4.Roles.Add(desk); // works there, doesn't own it
        Context.UserHospitalMappings.AddRange(m1, m2, m3, m4);

        Context.HospitalBillingSettings.Add(new HospitalBillingSettings
        {
            HospitalId = HospitalId, FrontDeskDiscountLimitPercent = 15, AccountantDiscountLimitPercent = 30,
            DiscountReasonRequired = true, PartnerPayWhenBilled = false, TdsCommissionRate = 2,
        });
        Context.ServiceCharges.AddRange(
            new ServiceCharge { HospitalId = HospitalId, Modality = "MRI", ServiceName = "MRI Brain", Amount = 6500, ReferralCutValue = 650 },
            new ServiceCharge { HospitalId = HospitalId, Modality = "CT", ServiceName = "CT Chest", Amount = 3200 },
            new ServiceCharge { HospitalId = _second, Modality = "mri", ServiceName = "mri brain ", Amount = 6000, TemplateId = Guid.NewGuid() },
            new ServiceCharge { HospitalId = _second, Modality = "USG", ServiceName = "USG Abdomen", Amount = 900 });
        await Context.SaveChangesAsync();
    }

    [Fact]
    public async Task Owner_sees_only_centres_where_they_are_admin_main_first()
    {
        await SeedAsync();
        var mine = await OwnerAccess.CentresAsync(Context, UserId, default);
        mine.Select(c => c.Name).Should().Equal("Main Road", "Lake Town", "Salt Lake");

        await FluentActions.Invoking(() => OwnerAccess.RequireAsync(Context, UserId, new[] { HospitalId, _notMine }, default))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Copy_sets_the_same_rules_and_prices_and_never_deletes_tests()
    {
        await SeedAsync();
        var res = await ChainSetup.CopyAsync(Context, HospitalId, new[] { _second }, billing: true, prices: true, keepLinked: true, UserId, default);
        await Context.SaveChangesAsync();

        res.Single().Should().Be(new ChainCopyResult(_second, true, 1, 1, 1));
        var s = await Context.HospitalBillingSettings.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == _second);
        s.FrontDeskDiscountLimitPercent.Should().Be(15);
        s.AccountantDiscountLimitPercent.Should().Be(30);
        s.DiscountReasonRequired.Should().BeTrue();
        s.PartnerPayWhenBilled.Should().BeFalse();
        s.TdsCommissionRate.Should().Be(2);
        (await Context.Hospitals.SingleAsync(h => h.HospitalId == _second)).IsAutoBillingEnabled.Should().BeTrue("automatic billing is a billing rule too");

        var prices = await Context.ServiceCharges.IgnoreQueryFilters().Where(x => x.HospitalId == _second).ToListAsync();
        prices.Should().HaveCount(3, "USG Abdomen stays — it is only at Lake Town and may be on old bills");
        var brain = prices.Single(p => p.ServiceName.Trim().Equals("mri brain", StringComparison.OrdinalIgnoreCase));
        brain.Amount.Should().Be(6500);
        brain.ReferralCutValue.Should().Be(650);
        brain.TemplateId.Should().NotBeNull("each centre keeps its own report template");
        prices.Single(p => p.ServiceName == "CT Chest").TemplateId.Should().BeNull();

        var link = await Context.HospitalSettingsLinks.SingleAsync();
        link.HospitalId.Should().Be(_second);
        link.SourceHospitalId.Should().Be(HospitalId);
        link.LinkBilling.Should().BeTrue();
        link.LinkPrices.Should().BeTrue();
    }

    [Fact]
    public async Task Copy_once_does_not_link()
    {
        await SeedAsync();
        await ChainSetup.CopyAsync(Context, HospitalId, new[] { _second }, true, false, keepLinked: false, UserId, default);
        await Context.SaveChangesAsync();
        (await Context.HospitalSettingsLinks.CountAsync()).Should().Be(0);
        (await Context.ServiceCharges.IgnoreQueryFilters().CountAsync(x => x.HospitalId == _second)).Should().Be(2, "prices weren't picked");
    }

    [Fact]
    public async Task Links_never_chain()
    {
        await SeedAsync();
        await ChainSetup.CopyAsync(Context, HospitalId, new[] { _second }, true, true, true, UserId, default);
        await Context.SaveChangesAsync();

        // Lake Town follows Main Road, so it can't be a main centre itself…
        await FluentActions.Invoking(() => ChainSetup.CopyAsync(Context, _second, new[] { _third }, true, true, true, UserId, default))
            .Should().ThrowAsync<ValidationException>();
        // …and Main Road can't be made to follow its own follower.
        await FluentActions.Invoking(() => ChainSetup.CopyAsync(Context, _third, new[] { HospitalId }, true, true, true, UserId, default))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Saving_at_the_main_centre_reaches_linked_centres_only()
    {
        await SeedAsync();
        await ChainSetup.CopyAsync(Context, HospitalId, new[] { _second }, true, true, true, UserId, default);
        await ChainSetup.CopyAsync(Context, HospitalId, new[] { _third }, true, true, keepLinked: false, UserId, default);
        await Context.SaveChangesAsync();

        var main = await Context.HospitalBillingSettings.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == HospitalId);
        main.FrontDeskDiscountLimitPercent = 5;
        await Context.SaveChangesAsync();
        await ChainSetup.PushBillingAsync(Context, HospitalId, default);

        (await Context.HospitalBillingSettings.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == _second)).FrontDeskDiscountLimitPercent.Should().Be(5);
        (await Context.HospitalBillingSettings.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == _third)).FrontDeskDiscountLimitPercent.Should().Be(15, "copied once, not linked");

        // A renamed and re-priced test follows by its old name.
        var ct = await Context.ServiceCharges.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == HospitalId && x.ServiceName == "CT Chest");
        var oldKey = ChainSetup.Key(ct.Modality, ct.ServiceName);
        ct.ServiceName = "CT Chest (plain)";
        ct.Amount = 3500;
        await Context.SaveChangesAsync();
        await ChainSetup.PushPriceAsync(Context, ct, oldKey, default);

        var followed = await Context.ServiceCharges.IgnoreQueryFilters().Where(x => x.HospitalId == _second && x.Modality == "CT").ToListAsync();
        followed.Should().ContainSingle().Which.Should().Match<ServiceCharge>(x => x.ServiceName == "CT Chest (plain)" && x.Amount == 3500);
        (await Context.ServiceCharges.IgnoreQueryFilters().SingleAsync(x => x.HospitalId == _third && x.Modality == "CT")).Amount.Should().Be(3200);
    }

    [Fact]
    public void Acting_centre_overrides_the_token_centre_and_nests()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("cid", HospitalId.ToString()) }, "test")),
        };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(http);
        var acting = new ActingCentre();
        var ctx = new UserContext(accessor.Object, acting);

        ctx.HospitalId.Should().Be(HospitalId);
        using (acting.Act(_second))
        {
            ctx.HospitalId.Should().Be(_second);
            using (acting.Act(_third)) ctx.HospitalId.Should().Be(_third);
            ctx.HospitalId.Should().Be(_second);
        }
        ctx.HospitalId.Should().Be(HospitalId);
    }
}
