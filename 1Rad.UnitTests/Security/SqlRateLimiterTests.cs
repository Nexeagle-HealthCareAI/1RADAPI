using _1Rad.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace _1Rad.UnitTests.Security;

// Shared rate limiter (gap analysis P3-9). The InMemory path mirrors the SQL counter.
public class SqlRateLimiterTests : BaseHandlerTest
{
    [Fact]
    public async Task Allows_up_to_the_limit_then_refuses_with_retry_after()
    {
        var limiter = new SqlRateLimiter(Context, NullLogger<SqlRateLimiter>.Instance);
        var key = "test:" + Guid.NewGuid();

        for (var i = 0; i < 3; i++)
            (await limiter.TryAcquireAsync(key, 3, TimeSpan.FromMinutes(10), default)).Allowed.Should().BeTrue();

        var (allowed, retryAfter) = await limiter.TryAcquireAsync(key, 3, TimeSpan.FromMinutes(10), default);
        allowed.Should().BeFalse();
        retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task Keys_are_independent()
    {
        var limiter = new SqlRateLimiter(Context, NullLogger<SqlRateLimiter>.Instance);
        var a = "a:" + Guid.NewGuid();
        var b = "b:" + Guid.NewGuid();
        (await limiter.TryAcquireAsync(a, 1, TimeSpan.FromMinutes(1), default)).Allowed.Should().BeTrue();
        (await limiter.TryAcquireAsync(a, 1, TimeSpan.FromMinutes(1), default)).Allowed.Should().BeFalse();
        (await limiter.TryAcquireAsync(b, 1, TimeSpan.FromMinutes(1), default)).Allowed.Should().BeTrue();
    }
}
