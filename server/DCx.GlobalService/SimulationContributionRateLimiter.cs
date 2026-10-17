using System.Net;
using System.Security.Cryptography;

namespace DCx.GlobalService;

internal sealed record SimulationContributionRateLimitOptions(
    int RequestsPerMinute,
    int MaximumConcurrentRequests)
{
    public static SimulationContributionRateLimitOptions Load() => new(
        ReadBoundedInt("DCX_GLOBAL_CONTRIBUTION_REQUESTS_PER_MINUTE", 120, 10, 10_000),
        ReadBoundedInt("DCX_GLOBAL_CONTRIBUTION_MAX_CONCURRENT", 4, 1, 32));

    private static int ReadBoundedInt(string name, int defaultValue, int minimum, int maximum)
    {
        var text = Environment.GetEnvironmentVariable(name);
        if (text is null) return defaultValue;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"{name} must be an integer from {minimum} through {maximum}.");
        }

        return value;
    }
}

internal sealed record SimulationContributionLimitResult(
    IDisposable? Lease,
    string? ErrorCode);

internal sealed class SimulationContributionRequestLimiter(
    SimulationContributionRateLimitOptions options)
{
    private const int MaximumPartitions = 1024;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object sync = new();
    private readonly Dictionary<string, PartitionState> partitions = new(StringComparer.Ordinal);

    public SimulationContributionLimitResult TryAcquire(
        IPAddress? remoteAddress,
        DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now == DateTimeOffset.MinValue)
        {
            return new SimulationContributionLimitResult(null, "rate_limit_time_invalid");
        }

        var key = PartitionKey(remoteAddress);
        lock (sync)
        {
            if (!partitions.TryGetValue(key, out var state))
            {
                RemoveExpiredInactivePartitions(now);
                if (partitions.Count >= MaximumPartitions)
                {
                    return new SimulationContributionLimitResult(null, "rate_limit_capacity");
                }

                state = new PartitionState(now);
                partitions.Add(key, state);
            }

            if (now < state.WindowStartedUtc)
            {
                return new SimulationContributionLimitResult(null, "rate_limit_time_invalid");
            }

            if (now >= state.WindowStartedUtc.Add(Window))
            {
                state.WindowStartedUtc = now;
                state.RequestCount = 0;
            }

            if (state.RequestCount >= options.RequestsPerMinute)
            {
                return new SimulationContributionLimitResult(null, "rate_limited");
            }

            if (state.ActiveRequests >= options.MaximumConcurrentRequests)
            {
                return new SimulationContributionLimitResult(null, "contribution_busy");
            }

            state.RequestCount++;
            state.ActiveRequests++;
            return new SimulationContributionLimitResult(
                new RequestLease(this, key),
                null);
        }
    }

    private void Release(string key)
    {
        lock (sync)
        {
            if (!partitions.TryGetValue(key, out var state) || state.ActiveRequests <= 0)
            {
                return;
            }

            state.ActiveRequests--;
        }
    }

    private void RemoveExpiredInactivePartitions(DateTimeOffset now)
    {
        foreach (var key in partitions
                     .Where(item =>
                         item.Value.ActiveRequests == 0 &&
                         now >= item.Value.WindowStartedUtc.Add(Window))
                     .Select(item => item.Key)
                     .ToArray())
        {
            partitions.Remove(key);
        }
    }

    private static string PartitionKey(IPAddress? address)
    {
        var canonical = address?.MapToIPv6().GetAddressBytes() ?? "unknown"u8.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private sealed class PartitionState(DateTimeOffset windowStartedUtc)
    {
        public DateTimeOffset WindowStartedUtc { get; set; } = windowStartedUtc;
        public int RequestCount { get; set; }
        public int ActiveRequests { get; set; }
    }

    private sealed class RequestLease(
        SimulationContributionRequestLimiter owner,
        string key) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(key);
            }
        }
    }
}

internal sealed class SimulationContributionRateLimitFilter(
    SimulationContributionRequestLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var result = limiter.TryAcquire(
            context.HttpContext.Connection.RemoteIpAddress,
            DateTimeOffset.UtcNow);
        if (result.Lease is null)
        {
            return Results.Json(
                new ContributionApiError(result.ErrorCode ?? "rate_limited"),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        using (result.Lease)
        {
            return await next(context).ConfigureAwait(false);
        }
    }
}
