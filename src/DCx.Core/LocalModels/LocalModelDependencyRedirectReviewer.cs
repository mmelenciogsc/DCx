using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public sealed record LocalModelDependencyRedirectReviewApproval(
    int SchemaVersion,
    string PlanSha256,
    bool Approved)
{
    public const int CurrentSchemaVersion = 1;

    public static LocalModelDependencyRedirectReviewApproval Create(
        LocalModelDependencyTransferPlan plan,
        bool approved)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        return new LocalModelDependencyRedirectReviewApproval(
            CurrentSchemaVersion,
            plan.PlanSha256,
            approved);
    }
}

public sealed class LocalModelDependencyRedirectReviewSession
{
    internal LocalModelDependencyRedirectReviewSession(
        string planSha256,
        LocalModelDependencyRedirectPolicyReview policyReview,
        DateTimeOffset observedAtUtc,
        DateTimeOffset expiresAtUtc,
        int itemCount,
        int requestCount,
        int redirectCount,
        int exactLengthCount,
        TimeProvider timeProvider)
    {
        LocalModelDependencyCatalogLoader.ValidateSha256(planSha256, "redirect review plan");
        LocalModelDependencyRedirectPolicyReview.Validate(policyReview);
        if (observedAtUtc.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= observedAtUtc || expiresAtUtc - observedAtUtc > LocalModelDependencyRedirectReviewer.MaximumSessionLifetime ||
            itemCount <= 0 || requestCount < itemCount || redirectCount < 0 || exactLengthCount < 0 ||
            exactLengthCount > itemCount || requestCount != itemCount + redirectCount)
        {
            throw new ArgumentException("The dependency redirect review session is invalid.");
        }

        PlanSha256 = planSha256;
        PolicyReview = policyReview;
        ObservedAtUtc = observedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ItemCount = itemCount;
        RequestCount = requestCount;
        RedirectCount = redirectCount;
        ExactLengthCount = exactLengthCount;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private readonly TimeProvider timeProvider;

    public string PlanSha256 { get; }
    public LocalModelDependencyRedirectPolicyReview PolicyReview { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public int ItemCount { get; }
    public int RequestCount { get; }
    public int RedirectCount { get; }
    public int ExactLengthCount { get; }
    public bool IsCurrent => timeProvider.GetUtcNow() < ExpiresAtUtc;

    internal ILocalModelDependencyRedirectPolicy CreatePolicy() =>
        new SessionLocalModelDependencyRedirectPolicy(this, timeProvider);
}

public sealed class LocalModelDependencyRedirectReviewException : Exception
{
    public LocalModelDependencyRedirectReviewException(string code, Exception? innerException = null)
        : base(code, innerException) => Code = code;

    public string Code { get; }
}

public interface ILocalModelDependencyRedirectReviewer : IDisposable
{
    Task<LocalModelDependencyRedirectReviewSession> ReviewEmbeddedAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectReviewApproval approval,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Observes current redirect transitions with bounded HEAD requests. It reads no response body,
/// retains no query value and grants no payload-transfer authority.
/// </summary>
public sealed class LocalModelDependencyRedirectReviewer : ILocalModelDependencyRedirectReviewer
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(2);
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromHours(2);
    private const int MaximumRedirects = 3;

    private readonly HttpClient httpClient;
    private readonly LocalModelDependencyCatalogReview? trustedCatalog;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan timeout;
    private readonly TimeSpan sessionLifetime;

    public LocalModelDependencyRedirectReviewer()
        : this(
            LocalModelDependencyHttpTransfer.CreateHardenedHandler(),
            trustedCatalog: null,
            TimeProvider.System,
            DefaultTimeout,
            DefaultSessionLifetime)
    {
    }

    internal LocalModelDependencyRedirectReviewer(
        HttpMessageHandler handler,
        LocalModelDependencyCatalogReview? trustedCatalog,
        TimeProvider timeProvider,
        TimeSpan timeout,
        TimeSpan sessionLifetime)
    {
        if (timeout <= TimeSpan.Zero || timeout > DefaultTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        if (sessionLifetime <= TimeSpan.Zero || sessionLifetime > MaximumSessionLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));
        }
        httpClient = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.trustedCatalog = trustedCatalog;
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.timeout = timeout;
        this.sessionLifetime = sessionLifetime;
    }

    public async Task<LocalModelDependencyRedirectReviewSession> ReviewEmbeddedAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectReviewApproval approval,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var catalog = trustedCatalog ??
                await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync(deadline.Token)
                    .ConfigureAwait(false);
            ValidateRequest(catalog, plan, approval);

            var routes = new List<LocalModelDependencyRedirectRouteReview>();
            var requests = 0;
            var redirects = 0;
            var exactLengths = 0;
            foreach (var item in plan.Items)
            {
                var currentUri = item.SourceUri;
                var visited = new HashSet<string>(StringComparer.Ordinal) { currentUri.AbsoluteUri };
                for (var redirectNumber = 0; ;)
                {
                    using var request = CreateHeadRequest(currentUri);
                    using var response = await httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            deadline.Token)
                        .ConfigureAwait(false);
                    requests++;
                    LocalModelDependencyHttpTransfer.ValidateResponseRequestUri(response, currentUri);
                    if (!LocalModelDependencyHttpTransfer.IsRedirect(response.StatusCode))
                    {
                        if (response.StatusCode != HttpStatusCode.OK ||
                            response.Content.Headers.ContentRange is not null ||
                            response.Content.Headers.ContentEncoding.Count != 0 ||
                            response.Headers.TransferEncoding.Count != 0 ||
                            response.Content.Headers.ContentLength is { } length && length != item.ExpectedBytes)
                        {
                            throw new LocalModelDependencyRedirectReviewException(
                                "dependency_redirect_review_response_invalid");
                        }
                        if (response.Content.Headers.ContentLength == item.ExpectedBytes)
                        {
                            exactLengths++;
                        }
                        break;
                    }

                    redirectNumber++;
                    redirects++;
                    if (redirectNumber > MaximumRedirects)
                    {
                        throw new LocalModelDependencyRedirectReviewException(
                            "dependency_redirect_review_redirect_limit");
                    }
                    var target = response.Headers.Location;
                    if (!LocalModelDependencyHttpTransfer.IsStructurallySafeRedirectTarget(target) ||
                        !visited.Add(target!.AbsoluteUri))
                    {
                        throw new LocalModelDependencyRedirectReviewException(
                            "dependency_redirect_review_redirect_invalid");
                    }
                    routes.Add(new LocalModelDependencyRedirectRouteReview(
                        item.SourceUri,
                        item.ExpectedBytes,
                        item.Sha256,
                        WithoutQuery(currentUri),
                        WithoutQuery(target)));
                    currentUri = target;
                }
            }

            var observed = timeProvider.GetUtcNow();
            return new LocalModelDependencyRedirectReviewSession(
                plan.PlanSha256,
                LocalModelDependencyRedirectPolicyReview.Create(Array.AsReadOnly(routes.ToArray())),
                observed,
                observed + sessionLifetime,
                plan.Items.Count,
                requests,
                redirects,
                exactLengths,
                timeProvider);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new LocalModelDependencyRedirectReviewException(
                "dependency_redirect_review_timeout",
                exception);
        }
        catch (LocalModelDependencyRedirectReviewException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LocalModelDependencyRedirectReviewException(
                "dependency_redirect_review_failed",
                exception);
        }
    }

    public void Dispose() => httpClient.Dispose();

    private static void ValidateRequest(
        LocalModelDependencyCatalogReview catalog,
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectReviewApproval approval)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        ArgumentNullException.ThrowIfNull(approval);
        var expected = new LocalModelDependencyTransferPlanner().Create(
            catalog,
            plan.CandidateIds,
            plan.ObservedAvailableFreeBytes);
        if (!DigestEquals(expected.PlanSha256, plan.PlanSha256))
        {
            throw new LocalModelDependencyRedirectReviewException(
                "dependency_redirect_review_catalog_invalid");
        }
        if (approval.SchemaVersion != LocalModelDependencyRedirectReviewApproval.CurrentSchemaVersion ||
            !approval.Approved || !DigestEquals(approval.PlanSha256, plan.PlanSha256))
        {
            throw new LocalModelDependencyRedirectReviewException(
                "dependency_redirect_review_approval_invalid");
        }
    }

    private static HttpRequestMessage CreateHeadRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, uri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static Uri WithoutQuery(Uri uri) => new(uri.GetLeftPart(UriPartial.Path));

    private static bool DigestEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.ASCII.GetBytes(right ?? string.Empty);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}

internal sealed class SessionLocalModelDependencyRedirectPolicy(
    LocalModelDependencyRedirectReviewSession session,
    TimeProvider timeProvider) : ILocalModelDependencyRedirectPolicy
{
    public string? PlanSha256 => session.PlanSha256;

    public LocalModelDependencyRedirectPolicyReview Review =>
        timeProvider.GetUtcNow() < session.ExpiresAtUtc
            ? session.PolicyReview
            : LocalModelDependencyRedirectPolicyReview.DenyAll;
}
