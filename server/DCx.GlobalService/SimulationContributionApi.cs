using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;

namespace DCx.GlobalService;

internal sealed record SimulationContributionApiOptions(
    byte[] ContributorTokenSha256,
    string ContributorPseudonym,
    byte[] ReviewerTokenSha256,
    string ReviewerId)
{
    public static SimulationContributionApiOptions? Load()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DCX_GLOBAL_CONTRIBUTION_API"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var contributorHash = ReadDigest("DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256");
        var reviewerHash = ReadDigest("DCX_GLOBAL_REVIEWER_TOKEN_SHA256");
        var contributor = ReadIdentity("DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM");
        var reviewer = ReadIdentity("DCX_GLOBAL_REVIEWER_ID");
        if (CryptographicOperations.FixedTimeEquals(contributorHash, reviewerHash))
        {
            throw new InvalidOperationException("Simulation contribution roles must use different credential digests.");
        }

        return new SimulationContributionApiOptions(
            contributorHash, contributor, reviewerHash, reviewer);
    }

    private static byte[] ReadDigest(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException($"{name} must contain a SHA-256 digest.");
        }

        return Convert.FromHexString(value);
    }

    private static string ReadIdentity(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '.' and not '_' and not '-'))
        {
            throw new InvalidOperationException($"{name} must contain a bounded pseudonymous identifier.");
        }

        return value;
    }
}

internal sealed record ContributionApiError(string Code);

internal static class SimulationCcbSandboxConfiguration
{
    public static ICcbSandboxExecutor? Load(string governanceRoot)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DCX_GLOBAL_CCB_SANDBOX"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The configured contribution sandbox requires Linux.");
        }

        var quarantineRoot = Path.Combine(Path.GetFullPath(governanceRoot), "quarantine");
        Directory.CreateDirectory(quarantineRoot);
        var timeoutText = Require("DCX_GLOBAL_CCB_TIMEOUT_SECONDS");
        if (!int.TryParse(timeoutText, out var timeoutSeconds) || timeoutSeconds is < 1 or > 30)
        {
            throw new InvalidOperationException(
                "DCX_GLOBAL_CCB_TIMEOUT_SECONDS must be an integer from 1 through 30.");
        }

        return new BubblewrapCcbSandboxExecutor(new BubblewrapCcbSandboxOptions(
            Require("DCX_GLOBAL_CCB_BASH_PATH"),
            RequireDigest("DCX_GLOBAL_CCB_BASH_SHA256"),
            Require("DCX_GLOBAL_CCB_LAUNCHER_PATH"),
            RequireDigest("DCX_GLOBAL_CCB_LAUNCHER_SHA256"),
            Require("DCX_GLOBAL_CCB_BUBBLEWRAP_PATH"),
            RequireDigest("DCX_GLOBAL_CCB_BUBBLEWRAP_SHA256"),
            Require("DCX_GLOBAL_CCB_RUNTIME_PATH"),
            RequireDigest("DCX_GLOBAL_CCB_RUNTIME_SHA256"),
            quarantineRoot,
            TimeSpan.FromSeconds(timeoutSeconds)));
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required when the CCB sandbox is enabled.");

    private static string RequireDigest(string name)
    {
        var value = Require(name);
        if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
        {
            throw new InvalidOperationException($"{name} must contain a SHA-256 digest.");
        }

        return value.ToLowerInvariant();
    }
}

internal static class SimulationContributionApi
{
    public static void Map(
        WebApplication app,
        ContributionGovernanceService governance,
        SimulationContributionApiOptions options)
    {
        var group = app.MapGroup("/api/contributions");
        group.MapPost("/intake", async (HttpRequest request, CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Contributor);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                var bytes = await ReadBoundedRequestAsync(request, cancellationToken).ConfigureAwait(false);
                var envelope = ContributionTransportCodec.Decode(bytes);
                if (!string.Equals(
                        envelope.Submission.Candidate.ContributorPseudonym,
                        options.ContributorPseudonym,
                        StringComparison.Ordinal))
                {
                    return Error(StatusCodes.Status403Forbidden, "contributor_identity_mismatch");
                }

                var state = await governance.IntakeAsync(
                    envelope.Submission,
                    envelope.Artifact,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                return Results.Accepted(
                    $"/api/contributions/{Uri.EscapeDataString(envelope.Submission.SubmissionId)}",
                    new { envelope.Submission.SubmissionId, State = state });
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
            catch (InvalidDataException)
            {
                return Error(StatusCodes.Status400BadRequest, "contribution_request_invalid");
            }
        });

        group.MapGet("/pending", async (
            HttpRequest request,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Reviewer);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                return Results.Ok(await governance.GetPendingReviewItemsAsync(
                    limit ?? 50, cancellationToken).ConfigureAwait(false));
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
        });

        group.MapGet("/publication-queue", async (
            HttpRequest request,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Reviewer);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                return Results.Ok(await governance.GetPublicationQueueAsync(
                    limit ?? 50, cancellationToken).ConfigureAwait(false));
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
        });

        group.MapPost("/publication-queue/{operationId}/retry", async (
            string operationId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Reviewer);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                return Results.Ok(await governance.RetryDeadLetterAsync(
                    operationId,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
        });

        group.MapGet("/{submissionId}", async (
            string submissionId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, requiredRole: null);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                var item = await governance.GetReviewItemAsync(submissionId, cancellationToken)
                    .ConfigureAwait(false);
                if (authorization.Role == SimulationApiRole.Contributor &&
                    !string.Equals(
                        item.ContributorPseudonym,
                        options.ContributorPseudonym,
                        StringComparison.Ordinal))
                {
                    return Error(StatusCodes.Status404NotFound, "submission_missing");
                }

                return Results.Ok(item);
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
        });

        group.MapPost("/{submissionId}/approve", async (
            string submissionId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Reviewer);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                return Results.Ok(await governance.ApproveAndPublishAsync(
                    submissionId,
                    options.ReviewerId,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
            catch (GlobalReplicaException)
            {
                return Error(StatusCodes.Status409Conflict, "registry_operation_failed");
            }
        });

        group.MapPost("/{submissionId}/withdraw", async (
            string submissionId,
            HttpRequest request,
            CancellationToken cancellationToken) =>
        {
            var authorization = Authorize(request, options, SimulationApiRole.Contributor);
            if (authorization.Error is not null) return authorization.Error;
            try
            {
                return Results.Ok(await governance.WithdrawOwnedAsync(
                    submissionId,
                    options.ContributorPseudonym,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false));
            }
            catch (ContributionPolicyException exception)
            {
                return PolicyError(exception.Code);
            }
            catch (GlobalReplicaException)
            {
                return Error(StatusCodes.Status409Conflict, "registry_operation_failed");
            }
        });
    }

    private static async Task<byte[]> ReadBoundedRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var mediaType = request.ContentType is null
            ? null
            : MediaTypeHeaderValue.TryParse(request.ContentType, out var parsed) ? parsed.MediaType : null;
        if (!string.Equals(mediaType, ContributionTransportCodec.MediaType, StringComparison.OrdinalIgnoreCase) ||
            request.ContentLength is null ||
            request.ContentLength <= ContributionTransportCodec.HeaderBytes ||
            request.ContentLength > ContributionTransportCodec.MaximumEnvelopeBytes)
        {
            throw new InvalidDataException("The contribution request has an invalid type or length.");
        }

        using var output = new MemoryStream((int)request.ContentLength.Value);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > ContributionTransportCodec.MaximumEnvelopeBytes)
            {
                throw new InvalidDataException("The contribution request is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (output.Length != request.ContentLength)
        {
            throw new InvalidDataException("The contribution request length changed during transfer.");
        }

        return output.ToArray();
    }

    private static SimulationAuthorization Authorize(
        HttpRequest request,
        SimulationContributionApiOptions options,
        SimulationApiRole? requiredRole)
    {
        if (request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || header.Parameter.Length is < 32 or > 512 ||
            header.Parameter.Any(character => character is < '!' or > '~'))
        {
            return new SimulationAuthorization(
                null, Error(StatusCodes.Status401Unauthorized, "authentication_required"));
        }

        var tokenBytes = Encoding.UTF8.GetBytes(header.Parameter);
        var digest = SHA256.HashData(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        var contributor = CryptographicOperations.FixedTimeEquals(
            digest, options.ContributorTokenSha256);
        var reviewer = CryptographicOperations.FixedTimeEquals(
            digest, options.ReviewerTokenSha256);
        CryptographicOperations.ZeroMemory(digest);
        var role = contributor
            ? SimulationApiRole.Contributor
            : reviewer ? SimulationApiRole.Reviewer : (SimulationApiRole?)null;
        if (role is null)
        {
            return new SimulationAuthorization(
                null, Error(StatusCodes.Status401Unauthorized, "authentication_invalid"));
        }

        if (requiredRole is not null && role != requiredRole)
        {
            return new SimulationAuthorization(
                role, Error(StatusCodes.Status403Forbidden, "role_forbidden"));
        }

        return new SimulationAuthorization(role, null);
    }

    private static IResult PolicyError(string code) => code switch
    {
        "submission_missing" or "submission_owner_mismatch" =>
            Error(StatusCodes.Status404NotFound, "submission_missing"),
        "publication_dead_letter_missing" =>
            Error(StatusCodes.Status404NotFound, "publication_dead_letter_missing"),
        "submission_id_invalid" or "review_limit_invalid" or
            "contribution_envelope_invalid" or "contribution_envelope_size_invalid" or
            "publication_queue_limit_invalid" or "publication_operation_id_invalid" =>
            Error(StatusCodes.Status400BadRequest, code),
        _ => Error(StatusCodes.Status409Conflict, code),
    };

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new ContributionApiError(code), statusCode: statusCode);

    private enum SimulationApiRole
    {
        Contributor = 1,
        Reviewer = 2,
    }

    private sealed record SimulationAuthorization(SimulationApiRole? Role, IResult? Error);
}
