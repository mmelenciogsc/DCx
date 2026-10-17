using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Finance;

namespace DCx.GlobalService;

internal sealed record SimulationCareOperationsApiOptions(
    byte[] OperatorTokenSha256,
    string OperatorPseudonymSha256,
    bool ProcessingEnabled)
{
    public static SimulationCareOperationsApiOptions? Load()
    {
        var apiEnabled = Environment.GetEnvironmentVariable("DCX_GLOBAL_CARE_OPERATIONS_API");
        var processingEnabled = Environment.GetEnvironmentVariable("DCX_GLOBAL_CARE_PROCESSING");
        var digestText = Environment.GetEnvironmentVariable("DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256");
        var operatorPseudonymSha256 =
            Environment.GetEnvironmentVariable("DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256");
        if (apiEnabled is null && processingEnabled is null && digestText is null &&
            operatorPseudonymSha256 is null)
        {
            return null;
        }

        if (apiEnabled is not "1")
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_OPERATIONS_API must be 1 when CARE simulation settings are present.");
        }

        if (processingEnabled is not null and not "1")
        {
            throw new InvalidOperationException("DCX_GLOBAL_CARE_PROCESSING must be 1 or unset.");
        }

        if (digestText is null || digestText.Length != 64 || !digestText.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_OPERATOR_TOKEN_SHA256 must contain a SHA-256 digest.");
        }

        if (operatorPseudonymSha256 is null)
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CARE_OPERATOR_PSEUDONYM_SHA256 must contain a SHA-256 digest.");
        }

        return new SimulationCareOperationsApiOptions(
            Convert.FromHexString(digestText),
            CareOperatorActionAuditIntegrity.NormalizeOperatorPseudonymSha256(
                operatorPseudonymSha256),
            processingEnabled == "1");
    }
}

internal sealed record CareOperationsApiError(string Code);

internal static class SimulationCareOperationsApi
{
    private const int MaximumRequestBytes = 8 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static void Map(
        WebApplication app,
        DurableCareOperationsSource source,
        SimulationCareOperationsApiOptions options,
        SimulationContributionRequestLimiter rateLimiter)
    {
        var group = app.MapGroup("/api/care/operations");
        group.AddEndpointFilter(new SimulationCareOperationsRateLimitFilter(rateLimiter));
        group.MapGet("/health", async (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var error = Authorize(request, options);
            if (error is not null) return error;
            try
            {
                return Results.Ok(await source.RefreshAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidOperationException)
            {
                return Error(StatusCodes.Status409Conflict, "care_operations_state_invalid");
            }
        });
        group.MapPost("/process", async (
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authorizationError = Authorize(request, options);
            if (authorizationError is not null) return authorizationError;
            CareOperationsProcessRequest processRequest;
            try
            {
                processRequest = await ReadRequestAsync(request, cancellationToken).ConfigureAwait(false);
                CareOperationsPolicy.RequireHealth(processRequest.ReviewedHealth);
            }
            catch (InvalidDataException)
            {
                return Error(StatusCodes.Status400BadRequest, "care_operations_request_invalid");
            }
            catch (IOException)
            {
                return Error(StatusCodes.Status400BadRequest, "care_operations_request_invalid");
            }
            catch (InvalidOperationException)
            {
                return Error(StatusCodes.Status400BadRequest, "care_operations_request_invalid");
            }
            catch (ArgumentException)
            {
                return Error(StatusCodes.Status400BadRequest, "care_operations_request_invalid");
            }

            try
            {
                return Results.Ok(await source.ProcessDueAsync(
                        processRequest.ReviewedHealth,
                        processRequest.ExplicitlyApproved,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message switch
                {
                    "Explicit approval is required to process verified CARE events." =>
                        Error(StatusCodes.Status403Forbidden, "care_operations_approval_required"),
                    "CARE verified-event processing capability is disabled." =>
                        Error(StatusCodes.Status409Conflict, "care_operations_processing_disabled"),
                    "The reviewed CARE operations state changed before processing." =>
                        Error(StatusCodes.Status409Conflict, "care_operations_review_stale"),
                    "The reviewed CARE operations state has no processable events." =>
                        Error(StatusCodes.Status409Conflict, "care_operations_nothing_due"),
                    _ => Error(StatusCodes.Status409Conflict, "care_operations_processing_failed"),
                };
            }
        });
    }

    private static IResult? Authorize(
        HttpRequest request,
        SimulationCareOperationsApiOptions options)
    {
        if (request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || header.Parameter.Length is < 32 or > 512 ||
            header.Parameter.Any(character => character is < '!' or > '~'))
        {
            return Error(StatusCodes.Status401Unauthorized, "authentication_required");
        }

        var tokenBytes = Encoding.UTF8.GetBytes(header.Parameter);
        var digest = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var authorized = CryptographicOperations.FixedTimeEquals(
            digest, options.OperatorTokenSha256);
        CryptographicOperations.ZeroMemory(digest);
        return authorized
            ? null
            : Error(StatusCodes.Status401Unauthorized, "authentication_invalid");
    }

    private static async Task<CareOperationsProcessRequest> ReadRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var mediaType = request.ContentType is null
            ? null
            : MediaTypeHeaderValue.TryParse(request.ContentType, out var parsed)
                ? parsed.MediaType
                : null;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            request.ContentLength is null or <= 0 or > MaximumRequestBytes)
        {
            throw new InvalidDataException("The CARE operations request type or length is invalid.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)request.ContentLength.Value);
        try
        {
            await request.Body.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var extra = new byte[1];
            try
            {
                if (await request.Body.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidDataException("The CARE operations request length changed.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(extra);
            }

            try
            {
                return JsonSerializer.Deserialize<CareOperationsProcessRequest>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("The CARE operations request is empty.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException("The CARE operations request JSON is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new CareOperationsApiError(code), statusCode: statusCode);

    private sealed class SimulationCareOperationsRateLimitFilter(
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
                    ? "care_operations_busy"
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
