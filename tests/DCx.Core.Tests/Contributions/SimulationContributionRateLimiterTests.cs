using System.Net;
using DCx.GlobalService;

namespace DCx.Core.Tests.Contributions;

public sealed class SimulationContributionRateLimiterTests
{
    [Fact]
    public void TryAcquire_EnforcesConcurrencyRatePartitionsAndUtcMonotonicTime()
    {
        var limiter = new SimulationContributionRequestLimiter(
            new SimulationContributionRateLimitOptions(2, 1));
        var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
        var firstAddress = IPAddress.Parse("192.0.2.10");

        var first = limiter.TryAcquire(firstAddress, now);
        Assert.NotNull(first.Lease);
        Assert.Null(first.ErrorCode);

        var busy = limiter.TryAcquire(firstAddress, now);
        Assert.Null(busy.Lease);
        Assert.Equal("contribution_busy", busy.ErrorCode);

        first.Lease.Dispose();
        first.Lease.Dispose();

        var second = limiter.TryAcquire(firstAddress, now.AddSeconds(1));
        Assert.NotNull(second.Lease);
        second.Lease!.Dispose();

        var limited = limiter.TryAcquire(firstAddress, now.AddSeconds(2));
        Assert.Null(limited.Lease);
        Assert.Equal("rate_limited", limited.ErrorCode);

        var independent = limiter.TryAcquire(IPAddress.Parse("192.0.2.11"), now.AddSeconds(2));
        Assert.NotNull(independent.Lease);
        independent.Lease!.Dispose();

        var nextWindow = limiter.TryAcquire(firstAddress, now.AddMinutes(1));
        Assert.NotNull(nextWindow.Lease);
        nextWindow.Lease!.Dispose();

        var nonUtc = limiter.TryAcquire(
            firstAddress,
            new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.FromHours(8)));
        Assert.Null(nonUtc.Lease);
        Assert.Equal("rate_limit_time_invalid", nonUtc.ErrorCode);

        var regressed = limiter.TryAcquire(firstAddress, now.AddSeconds(-1));
        Assert.Null(regressed.Lease);
        Assert.Equal("rate_limit_time_invalid", regressed.ErrorCode);
    }
}
