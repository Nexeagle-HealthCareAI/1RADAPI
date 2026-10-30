using _1Rad.Domain.Constants;
using FluentAssertions;
using Xunit;

namespace _1Rad.UnitTests.Security;

// Gap analysis P0-5 / P1-2: custom roles store page routes, the sentinel sees API paths.
public class CustomRoleRouteMapTests
{
    [Theory]
    [InlineData("/billing", "/api/v1/finance/payments")]
    [InlineData("/billing", "/api/v1/referrers/commissions/pay")]
    [InlineData("/staff", "/api/v1/attendance/6f2c1f8e-0000-0000-0000-000000000000")]
    [InlineData("/staff", "/api/v1/leave-policy")]
    [InlineData("/configuration", "/api/v1/reporting/templates/upsert")]
    [InlineData("/appointment-board", "/api/v1/finance/registry/quick-add")]
    [InlineData("BILLING", "api/v1/Finance/Expense")]
    public void Grants_page_route_to_its_api_area(string page, string path)
    {
        CustomRoleRouteMap.Grants(new[] { page }, path).Should().BeTrue();
    }

    [Theory]
    [InlineData("/operations-board", "/api/v1/finance/payments")]
    [InlineData("/configuration", "/api/v1/reporting/report/finalize")]
    [InlineData("/appointment-board", "/api/v1/finance/registry")]       // full registry CRUD is admin-board
    [InlineData("/appointment-board", "/api/v1/finance/expense")]
    [InlineData("/staff", "/api/v1/staffing")]                            // segment-aware, not raw prefix
    public void Does_not_grant_other_areas(string page, string path)
    {
        CustomRoleRouteMap.Grants(new[] { page }, path).Should().BeFalse();
    }

    [Theory]
    [InlineData("api")]
    [InlineData("/api/v1")]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/not-a-page")]
    public void Arbitrary_strings_grant_nothing_and_are_not_known_pages(string saved)
    {
        CustomRoleRouteMap.Grants(new[] { saved }, "/api/v1/subscriptions/modules").Should().BeFalse();
        CustomRoleRouteMap.IsKnownPage(saved).Should().BeFalse();
    }

    [Fact]
    public void Normalise_makes_routes_comparable()
    {
        CustomRoleRouteMap.Normalise(" Billing/ ").Should().Be("/billing");
        CustomRoleRouteMap.IsKnownPage("billing").Should().BeTrue();
    }
}
