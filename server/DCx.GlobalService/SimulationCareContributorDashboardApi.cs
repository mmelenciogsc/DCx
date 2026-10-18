using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Finance;

namespace DCx.GlobalService;

internal sealed record SimulationCareContributorDashboardApiOptions(
    byte[] ContributorTokenSha256,
    string ContributorPseudonym)
{
    public static SimulationCareContributorDashboardApiOptions? Load()
        => Parse(
            Environment.GetEnvironmentVariable(
                "DCX_GLOBAL_CARE_CONTRIBUTOR_DASHBOARD_API"),
            Environment.GetEnvironmentVariable(
                "DCX_GLOBAL_CARE_CONTRIBUTOR_TOKEN_SHA256"),
            Environment.GetEnvironmentVariable(
                "DCX_GLOBAL_CARE_CONTRIBUTOR_PSEUDONYM"));

    internal static SimulationCareContributorDashboardApiOptions? Parse(
        string? apiEnabled,
        string? digestText,
        string? contributorPseudonym)
    {
        if (apiEnabled is null && digestText is null && contributorPseudonym is null)
        {
            return null;
        }

        if (apiEnabled is not "1")
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_CONTRIBUTOR_DASHBOARD_API must be 1 when contributor simulation settings are present.");
        }

        if (digestText is null || digestText.Length != 64 ||
            !digestText.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_CONTRIBUTOR_TOKEN_SHA256 must contain a SHA-256 digest.");
        }

        PayPalSandboxValidation.RequireContributorPseudonym(contributorPseudonym ?? string.Empty);
        return new SimulationCareContributorDashboardApiOptions(
            Convert.FromHexString(digestText),
            contributorPseudonym!);
    }
}

internal sealed record CareContributorDashboardApiError(string Code);

internal static class SimulationCareContributorDashboardApi
{
    public static void Map(
        WebApplication app,
        DurableCareContributorDashboardSource source,
        SimulationCareContributorDashboardApiOptions options,
        SimulationContributionRequestLimiter rateLimiter)
    {
        var group = app.MapGroup("/api/care/contributor-dashboard");
        group.AddEndpointFilter(new RateLimitFilter(rateLimiter));
        group.MapGet("", async (
            HttpRequest request,
            HttpResponse response,
            CancellationToken cancellationToken) =>
        {
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
            var error = Authorize(request, options);
            if (error is not null) return error;

            try
            {
                var snapshot = await source.LoadAsync(cancellationToken).ConfigureAwait(false);
                CareContributorDashboardPolicy.RequireSnapshot(snapshot);
                if (snapshot.StatusCode != CareContributorDashboardPolicy.Available ||
                    !string.Equals(
                        snapshot.ContributorPseudonym,
                        options.ContributorPseudonym,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The CARE contributor dashboard subject binding is invalid.");
                }

                return Results.Ok(snapshot);
            }
            catch (InvalidOperationException)
            {
                return Error(
                    StatusCodes.Status409Conflict,
                    "care_contributor_state_invalid");
            }
        });
    }

    private static IResult? Authorize(
        HttpRequest request,
        SimulationCareContributorDashboardApiOptions options)
    {
        if (request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(
                request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            header.Parameter.Length is < 32 or > 512 ||
            header.Parameter.Any(character => character is < '!' or > '~'))
        {
            return Error(StatusCodes.Status401Unauthorized, "authentication_required");
        }

        var tokenBytes = Encoding.UTF8.GetBytes(header.Parameter);
        var digest = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var authorized = CryptographicOperations.FixedTimeEquals(
            digest,
            options.ContributorTokenSha256);
        CryptographicOperations.ZeroMemory(digest);
        return authorized
            ? null
            : Error(StatusCodes.Status401Unauthorized, "authentication_invalid");
    }

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new CareContributorDashboardApiError(code), statusCode: statusCode);

    private sealed class RateLimitFilter(
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
                var code = result.ErrorCode == "contribution_busy"
                    ? "care_contributor_busy"
                    : result.ErrorCode ?? "rate_limited";
                return Error(StatusCodes.Status429TooManyRequests, code);
            }

            using (result.Lease)
            {
                return await next(context).ConfigureAwait(false);
            }
        }
    }
}
