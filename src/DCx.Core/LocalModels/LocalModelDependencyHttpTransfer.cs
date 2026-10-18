using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public sealed record LocalModelDependencyTransferApproval(
    int SchemaVersion,
    string PlanSha256,
    string RedirectPolicySha256,
    Uri SourceUri,
    long ExpectedBytes,
    string Sha256,
    bool Approved)
{
    public const int CurrentSchemaVersion = 2;

    public static LocalModelDependencyTransferApproval Create(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferItem item,
        bool approved) => Create(
            plan,
            item,
            LocalModelDependencyRedirectPolicyReview.DenyAll,
            approved);

    public static LocalModelDependencyTransferApproval Create(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferItem item,
        LocalModelDependencyRedirectPolicyReview redirectPolicy,
        bool approved)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        ArgumentNullException.ThrowIfNull(item);
        LocalModelDependencyRedirectPolicyReview.Validate(redirectPolicy);
        if (!plan.Items.Any(candidate => SameIdentity(candidate, item)))
        {
            throw new InvalidOperationException("The dependency transfer approval item is not in the authenticated plan.");
        }
        return new LocalModelDependencyTransferApproval(
            CurrentSchemaVersion,
            plan.PlanSha256,
            redirectPolicy.PolicySha256,
            item.SourceUri,
            item.ExpectedBytes,
            item.Sha256,
            approved);
    }

    internal static bool SameIdentity(
        LocalModelDependencyTransferItem item,
        LocalModelDependencyTransferApproval approval) =>
        item.ExpectedBytes == approval.ExpectedBytes &&
        string.Equals(item.SourceUri.AbsoluteUri, approval.SourceUri.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(item.Sha256, approval.Sha256, StringComparison.Ordinal);

    private static bool SameIdentity(
        LocalModelDependencyTransferItem left,
        LocalModelDependencyTransferItem right) =>
        left.ExpectedBytes == right.ExpectedBytes &&
        string.Equals(left.SourceUri.AbsoluteUri, right.SourceUri.AbsoluteUri, StringComparison.Ordinal) &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
}

public sealed record LocalModelDependencyTransferResult(
    string PlanSha256,
    string RedirectPolicySha256,
    Uri SourceUri,
    long Bytes,
    string Sha256);

public sealed record LocalModelDependencyItemTransferProgress(
    long BytesTransferred,
    long ExpectedBytes);

public interface ILocalModelDependencyTransfer
{
    LocalModelDependencyRedirectPolicyReview RedirectPolicyReview { get; }

    Task<LocalModelDependencyTransferResult> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        Stream destination,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyItemTransferProgress>? progress = null);
}

public sealed record LocalModelDependencyRedirectRouteReview(
    Uri ItemSourceUri,
    long ExpectedBytes,
    string Sha256,
    Uri FromOrigin,
    Uri TargetUriPrefix);

public sealed record LocalModelDependencyRedirectPolicyReview(
    int SchemaVersion,
    string PolicySha256,
    bool AllowsRedirects,
    IReadOnlyList<LocalModelDependencyRedirectRouteReview> Routes)
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumRules = 16;
    private const string DigestDomain = "DCx.local-model-dependency-redirect-policy.v1";

    public static LocalModelDependencyRedirectPolicyReview DenyAll { get; } = Create([]);

    internal static LocalModelDependencyRedirectPolicyReview Create(
        IReadOnlyList<LocalModelDependencyRedirectRouteReview> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count > MaximumRules)
        {
            throw new ArgumentOutOfRangeException(nameof(routes));
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<(string Identity, LocalModelDependencyRedirectRouteReview Route)>(routes.Count);
        foreach (var route in routes)
        {
            ValidateRoute(route);
            var identity = RouteIdentity(route);
            if (!identities.Add(identity))
            {
                throw new ArgumentException("The redirect policy contains a duplicate rule.", nameof(routes));
            }
            ordered.Add((identity, route));
        }
        ordered.Sort((left, right) => StringComparer.Ordinal.Compare(left.Identity, right.Identity));
        var immutable = Array.AsReadOnly(ordered.Select(value => value.Route).ToArray());
        return new LocalModelDependencyRedirectPolicyReview(
            CurrentSchemaVersion,
            ComputeDigest(immutable),
            immutable.Count != 0,
            immutable);
    }

    internal static void Validate(LocalModelDependencyRedirectPolicyReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (review.SchemaVersion != CurrentSchemaVersion || review.Routes is null ||
            review.Routes.Count > MaximumRules || review.AllowsRedirects != (review.Routes.Count != 0))
        {
            throw new InvalidOperationException("The dependency redirect policy review is invalid.");
        }
        var expected = Create(review.Routes);
        if (!DigestEquals(expected.PolicySha256, review.PolicySha256) ||
            !expected.Routes.SequenceEqual(review.Routes))
        {
            throw new InvalidOperationException("The dependency redirect policy review digest is invalid.");
        }
    }

    internal bool IsAllowed(
        LocalModelDependencyTransferItem item,
        Uri previousUri,
        Uri targetUri,
        int redirectNumber)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(previousUri);
        ArgumentNullException.ThrowIfNull(targetUri);
        if (redirectNumber <= 0)
        {
            return false;
        }

        return Routes.Any(route =>
            route.ExpectedBytes == item.ExpectedBytes &&
            DigestEquals(route.Sha256, item.Sha256) &&
            string.Equals(
                route.ItemSourceUri.AbsoluteUri,
                item.SourceUri.AbsoluteUri,
                StringComparison.Ordinal) &&
            SameOrigin(route.FromOrigin, previousUri) &&
            SameOrigin(route.TargetUriPrefix, targetUri) &&
            targetUri.AbsolutePath.StartsWith(
                route.TargetUriPrefix.AbsolutePath,
                StringComparison.Ordinal));
    }

    internal static void ValidateOrigin(Uri uri, string parameterName)
    {
        ValidateRedirectUri(uri);
        if (!string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A redirect origin must have a root path.", parameterName);
        }
    }

    internal static void ValidateTargetPrefix(Uri uri)
    {
        ValidateRedirectUri(uri);
        if (string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("A redirect target prefix must be non-root and end with a slash.", nameof(uri));
        }
    }

    private static void ValidateRoute(LocalModelDependencyRedirectRouteReview route)
    {
        ArgumentNullException.ThrowIfNull(route);
        LocalModelDependencyCatalogLoader.ValidateUri(route.ItemSourceUri, "redirect item source");
        if (route.ExpectedBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(route));
        }
        LocalModelDependencyCatalogLoader.ValidateSha256(route.Sha256, "redirect item");
        ValidateOrigin(route.FromOrigin, nameof(route.FromOrigin));
        ValidateTargetPrefix(route.TargetUriPrefix);
    }

    private static void ValidateRedirectUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            Uri.CheckHostName(uri.IdnHost) != UriHostNameType.Dns ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath.Contains("//", StringComparison.Ordinal) ||
            uri.OriginalString.Contains('\\', StringComparison.Ordinal) ||
            uri.OriginalString.Any(char.IsControl) || uri.AbsoluteUri.Length > 4096)
        {
            throw new ArgumentException("The redirect policy URI is invalid.", nameof(uri));
        }
    }

    private static string RouteIdentity(LocalModelDependencyRedirectRouteReview route) => string.Join('\n',
        route.ItemSourceUri.AbsoluteUri,
        route.ExpectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        route.Sha256,
        route.FromOrigin.AbsoluteUri,
        route.TargetUriPrefix.AbsoluteUri);

    private static bool SameOrigin(Uri expected, Uri actual) =>
        expected.Scheme == actual.Scheme &&
        expected.Port == actual.Port &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.Ordinal);

    private static string ComputeDigest(IReadOnlyList<LocalModelDependencyRedirectRouteReview> routes)
    {
        var canonical = string.Join('\n', routes.Select(RouteIdentity).Prepend(DigestDomain));
        var bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

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

internal interface ILocalModelDependencyRedirectPolicy
{
    LocalModelDependencyRedirectPolicyReview Review { get; }
}

internal sealed class DenyAllLocalModelDependencyRedirectPolicy : ILocalModelDependencyRedirectPolicy
{
    public static DenyAllLocalModelDependencyRedirectPolicy Instance { get; } = new();

    private DenyAllLocalModelDependencyRedirectPolicy()
    {
    }

    public LocalModelDependencyRedirectPolicyReview Review =>
        LocalModelDependencyRedirectPolicyReview.DenyAll;
}

internal sealed record LocalModelDependencyRedirectRule(
    Uri ItemSourceUri,
    long ExpectedBytes,
    string Sha256,
    Uri FromOrigin,
    Uri TargetUriPrefix);

/// <summary>
/// Matches redirect transitions only for one exact authenticated plan item and one compiled
/// HTTPS origin/path transition. Production intentionally supplies no rules until official
/// provider behavior has been reviewed and approved.
/// </summary>
internal sealed class ExactLocalModelDependencyRedirectPolicy : ILocalModelDependencyRedirectPolicy
{
    private const int MaximumRules = 16;
    public LocalModelDependencyRedirectPolicyReview Review { get; }

    public ExactLocalModelDependencyRedirectPolicy(
        IReadOnlyList<LocalModelDependencyRedirectRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count is < 1 or > MaximumRules)
        {
            throw new ArgumentOutOfRangeException(nameof(rules));
        }

        var validated = new List<LocalModelDependencyRedirectRule>(rules.Count);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ArgumentNullException.ThrowIfNull(rule);
            LocalModelDependencyCatalogLoader.ValidateUri(rule.ItemSourceUri, "redirect item source");
            if (rule.ExpectedBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rules));
            }
            LocalModelDependencyCatalogLoader.ValidateSha256(rule.Sha256, "redirect item");
            LocalModelDependencyRedirectPolicyReview.ValidateOrigin(rule.FromOrigin, nameof(rule.FromOrigin));
            LocalModelDependencyRedirectPolicyReview.ValidateTargetPrefix(rule.TargetUriPrefix);
            var identity = string.Join('\n',
                rule.ItemSourceUri.AbsoluteUri,
                rule.ExpectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                rule.Sha256,
                rule.FromOrigin.AbsoluteUri,
                rule.TargetUriPrefix.AbsoluteUri);
            if (!identities.Add(identity))
            {
                throw new ArgumentException("The redirect policy contains a duplicate rule.", nameof(rules));
            }
            validated.Add(rule);
        }
        validated.Sort((left, right) => StringComparer.Ordinal.Compare(
            RuleIdentity(left),
            RuleIdentity(right)));
        Review = LocalModelDependencyRedirectPolicyReview.Create(Array.AsReadOnly(
            validated.Select(rule => new LocalModelDependencyRedirectRouteReview(
                rule.ItemSourceUri,
                rule.ExpectedBytes,
                rule.Sha256,
                rule.FromOrigin,
                rule.TargetUriPrefix)).ToArray()));
    }

    public bool IsAllowed(
        LocalModelDependencyTransferItem item,
        Uri previousUri,
        Uri targetUri,
        int redirectNumber)
    {
        return Review.IsAllowed(item, previousUri, targetUri, redirectNumber);
    }

    private static string RuleIdentity(LocalModelDependencyRedirectRule rule) => string.Join('\n',
        rule.ItemSourceUri.AbsoluteUri,
        rule.ExpectedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        rule.Sha256,
        rule.FromOrigin.AbsoluteUri,
        rule.TargetUriPrefix.AbsoluteUri);

}

public sealed class LocalModelDependencyTransferException : Exception
{
    public LocalModelDependencyTransferException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Streams one explicitly approved, digest-bound plan item into an empty rollback-capable stream.
/// It never chooses a filesystem path, installs, extracts, registers, executes, or persists approval.
/// </summary>
public sealed class LocalModelDependencyHttpTransfer : ILocalModelDependencyTransfer, IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(45);
    private const int BufferBytes = 64 * 1024;
    private const int MaximumRedirects = 3;

    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;
    private readonly LocalModelDependencyCatalogReview? trustedCatalog;
    private readonly ILocalModelDependencyRedirectPolicy redirectPolicy;

    public LocalModelDependencyRedirectPolicyReview RedirectPolicyReview => redirectPolicy.Review;

    public LocalModelDependencyHttpTransfer()
        : this(
            CreateHardenedHandler(),
            null,
            DefaultTimeout,
            DenyAllLocalModelDependencyRedirectPolicy.Instance,
            useInjectedCatalog: false)
    {
    }

    internal LocalModelDependencyHttpTransfer(
        HttpMessageHandler handler,
        LocalModelDependencyCatalogReview trustedCatalog,
        TimeSpan? timeout = null,
        ILocalModelDependencyRedirectPolicy? redirectPolicy = null)
        : this(
            handler,
            trustedCatalog ?? throw new ArgumentNullException(nameof(trustedCatalog)),
            timeout,
            redirectPolicy ?? DenyAllLocalModelDependencyRedirectPolicy.Instance,
            useInjectedCatalog: true)
    {
    }

    private LocalModelDependencyHttpTransfer(
        HttpMessageHandler handler,
        LocalModelDependencyCatalogReview? trustedCatalog,
        TimeSpan? timeout,
        ILocalModelDependencyRedirectPolicy redirectPolicy,
        bool useInjectedCatalog)
    {
        this.timeout = timeout ?? DefaultTimeout;
        if (this.timeout <= TimeSpan.Zero || this.timeout > DefaultTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        httpClient = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)), true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        this.trustedCatalog = useInjectedCatalog
            ? trustedCatalog ?? throw new ArgumentNullException(nameof(trustedCatalog))
            : null;
        this.redirectPolicy = redirectPolicy ?? throw new ArgumentNullException(nameof(redirectPolicy));
        LocalModelDependencyRedirectPolicyReview.Validate(this.redirectPolicy.Review);
    }

    public async Task<LocalModelDependencyTransferResult> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        Stream destination,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyItemTransferProgress>? progress = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var catalog = trustedCatalog ??
                await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync(deadline.Token)
                    .ConfigureAwait(false);
            var approvedRedirectPolicy = redirectPolicy.Review;
            LocalModelDependencyRedirectPolicyReview.Validate(approvedRedirectPolicy);
            var item = ValidateRequest(catalog, plan, approval, approvedRedirectPolicy, destination);
            using var response = await SendWithRedirectPolicyAsync(
                    item,
                    approvedRedirectPolicy,
                    deadline.Token)
                .ConfigureAwait(false);

            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token)
                .ConfigureAwait(false);
            var digest = await CopyAndHashAsync(
                    input,
                    destination,
                    item.ExpectedBytes,
                    deadline.Token,
                    progress)
                .ConfigureAwait(false);
            LocalModelDependencyCatalogLoader.RequireDigest(
                digest,
                item.Sha256,
                "transferred dependency");
            EnsureCurrentRedirectPolicy(approvedRedirectPolicy.PolicySha256);
            await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
            return new LocalModelDependencyTransferResult(
                plan.PlanSha256,
                approvedRedirectPolicy.PolicySha256,
                item.SourceUri,
                item.ExpectedBytes,
                digest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAsync(destination).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await RollBackAsync(destination).ConfigureAwait(false);
            throw new LocalModelDependencyTransferException("dependency_transfer_timeout", exception);
        }
        catch (LocalModelDependencyTransferException)
        {
            await RollBackAsync(destination).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or InvalidOperationException or CryptographicException)
        {
            await RollBackAsync(destination).ConfigureAwait(false);
            throw new LocalModelDependencyTransferException("dependency_transfer_failed", exception);
        }
    }

    public void Dispose() => httpClient.Dispose();

    internal static HttpMessageHandler CreateHardenedHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        Credentials = null,
        MaxResponseHeadersLength = 16,
        PreAuthenticate = false,
        UseCookies = false,
        UseProxy = false,
    };

    private async Task<HttpResponseMessage> SendWithRedirectPolicyAsync(
        LocalModelDependencyTransferItem item,
        LocalModelDependencyRedirectPolicyReview approvedRedirectPolicy,
        CancellationToken cancellationToken)
    {
        var currentUri = item.SourceUri;
        var visited = new HashSet<string>(StringComparer.Ordinal) { currentUri.AbsoluteUri };
        for (var redirectNumber = 0; ;)
        {
            using var request = CreateRequest(currentUri);
            var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                ValidateResponseRequestUri(response, currentUri);
                if (!IsRedirect(response.StatusCode))
                {
                    ValidateFinalResponse(response, item);
                    return response;
                }

                redirectNumber++;
                if (redirectNumber > MaximumRedirects)
                {
                    throw new LocalModelDependencyTransferException("dependency_transfer_redirect_limit");
                }
                var targetUri = response.Headers.Location;
                if (!IsStructurallySafeRedirectTarget(targetUri) ||
                    !visited.Add(targetUri!.AbsoluteUri))
                {
                    throw new LocalModelDependencyTransferException("dependency_transfer_redirect_invalid");
                }
                if (!approvedRedirectPolicy.IsAllowed(item, currentUri, targetUri, redirectNumber))
                {
                    throw new LocalModelDependencyTransferException("dependency_transfer_redirect_denied");
                }
                EnsureCurrentRedirectPolicy(approvedRedirectPolicy.PolicySha256);
                currentUri = targetUri;
            }
            catch
            {
                response.Dispose();
                throw;
            }
            response.Dispose();
        }
    }

    private static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsStructurallySafeRedirectTarget(Uri? uri) =>
        uri is
        {
            IsAbsoluteUri: true,
            IsDefaultPort: true,
        } &&
        uri.Scheme == Uri.UriSchemeHttps &&
        Uri.CheckHostName(uri.IdnHost) == UriHostNameType.Dns &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath.Length <= 1024 &&
        uri.Query.Length <= 2048 &&
        uri.AbsoluteUri.Length <= 4096 &&
        !uri.AbsolutePath.Contains("//", StringComparison.Ordinal) &&
        !uri.OriginalString.Contains("\\", StringComparison.Ordinal) &&
        !uri.OriginalString.Any(char.IsControl);

    private static void ValidateResponseRequestUri(HttpResponseMessage response, Uri requestedUri)
    {
        if (response.RequestMessage?.RequestUri is not { } responseUri ||
            !string.Equals(responseUri.AbsoluteUri, requestedUri.AbsoluteUri, StringComparison.Ordinal))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_response_invalid");
        }
    }

    private LocalModelDependencyTransferItem ValidateRequest(
        LocalModelDependencyCatalogReview trustedCatalog,
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        LocalModelDependencyRedirectPolicyReview redirectPolicyReview,
        Stream destination)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        ArgumentNullException.ThrowIfNull(trustedCatalog);
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(destination);
        var expectedPlan = new LocalModelDependencyTransferPlanner().Create(
            trustedCatalog,
            plan.CandidateIds,
            plan.ObservedAvailableFreeBytes);
        if (!DigestEquals(expectedPlan.PlanSha256, plan.PlanSha256))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_catalog_invalid");
        }
        if (!plan.CapacitySufficient || plan.ObservedAvailableFreeBytes is null)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_capacity_unapproved");
        }
        if (approval.SchemaVersion != LocalModelDependencyTransferApproval.CurrentSchemaVersion ||
            !approval.Approved ||
            !DigestEquals(approval.PlanSha256, plan.PlanSha256) ||
            !DigestEquals(approval.RedirectPolicySha256, redirectPolicyReview.PolicySha256))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
        }
        var item = plan.Items.SingleOrDefault(candidate =>
            LocalModelDependencyTransferApproval.SameIdentity(candidate, approval));
        if (item is null)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
        }
        if (!destination.CanWrite || !destination.CanSeek || destination.Length != 0 || destination.Position != 0)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_destination_invalid");
        }
        return item;
    }

    private void EnsureCurrentRedirectPolicy(string expectedSha256)
    {
        var current = redirectPolicy.Review;
        LocalModelDependencyRedirectPolicyReview.Validate(current);
        if (!DigestEquals(current.PolicySha256, expectedSha256))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
        }
    }

    private static void ValidateFinalResponse(
        HttpResponseMessage response,
        LocalModelDependencyTransferItem item)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentLength != item.ExpectedBytes ||
            response.Content.Headers.ContentRange is not null ||
            response.Content.Headers.ContentEncoding.Count != 0 ||
            response.Headers.TransferEncoding.Count != 0)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_response_invalid");
        }
    }

    private static async Task<string> CopyAndHashAsync(
        Stream input,
        Stream destination,
        long expectedBytes,
        CancellationToken cancellationToken,
        IProgress<LocalModelDependencyItemTransferProgress>? progress)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferBytes];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > expectedBytes)
                {
                    throw new LocalModelDependencyTransferException("dependency_transfer_length_invalid");
                }
                digest.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                progress?.Report(new LocalModelDependencyItemTransferProgress(total, expectedBytes));
            }
            if (total != expectedBytes)
            {
                throw new LocalModelDependencyTransferException("dependency_transfer_length_invalid");
            }
            return Convert.ToHexStringLower(digest.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task RollBackAsync(Stream destination)
    {
        try
        {
            destination.SetLength(0);
            destination.Position = 0;
            await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_cleanup_failed", exception);
        }
    }

    private static bool DigestEquals(string left, string right)
    {
        if (left is null || right is null) return false;
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
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
