using _1Rad.Application.Common;
using FluentAssertions;

namespace _1Rad.UnitTests.Common;

public class InvoiceNumberingTests
{
    [Theory]
    [InlineData("2026-04-01T00:00:00Z", "2026-27")]   // 05:30 IST on 1 Apr → new FY
    [InlineData("2026-03-31T18:29:59Z", "2025-26")]   // 23:59:59 IST on 31 Mar → old FY
    [InlineData("2026-03-31T18:30:00Z", "2026-27")]   // 00:00 IST on 1 Apr
    [InlineData("2027-01-15T10:00:00Z", "2026-27")]
    [InlineData("2099-12-31T00:00:00Z", "2099-00")]
    public void Financial_year_follows_IST_April_to_March(string utc, string expected)
    {
        var t = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        InvoiceNumbering.FinancialYearLabel(t).Should().Be(expected);
    }

    [Fact]
    public void Format_pads_the_running_number_and_stays_within_16_characters()
    {
        // Review B26: GST rules 46/49 allow at most 16 characters.
        InvoiceNumbering.Format("2026-27", 7).Should().Be("INV2627-00007");
        InvoiceNumbering.Format("2026-27", 123456).Should().Be("INV2627-123456");
        InvoiceNumbering.Format("2099-00", 9_999_999).Length.Should().BeLessThanOrEqualTo(16);
    }
}
