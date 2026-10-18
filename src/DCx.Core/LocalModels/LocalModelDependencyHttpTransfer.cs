using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.LocalModels;

public sealed record LocalModelDependencyTransferApproval(
    int SchemaVersion,
    string PlanSha256,
    Uri SourceUri,
    long ExpectedBytes,
    string Sha256,
    bool Approved)
{
    public const int CurrentSchemaVersion = 1;

    public static LocalModelDependencyTransferApproval Create(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferItem item,
        bool approved)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        ArgumentNullException.ThrowIfNull(item);
        if (!plan.Items.Any(candidate => SameIdentity(candidate, item)))
        {
            throw new InvalidOperationException("The dependency transfer approval item is not in the authenticated plan.");
        }
        return new LocalModelDependencyTransferApproval(
            CurrentSchemaVersion,
            plan.PlanSha256,
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
    Uri SourceUri,
    long Bytes,
    string Sha256);

public interface ILocalModelDependencyTransfer
{
    Task<LocalModelDependencyTransferResult> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        Stream destination,
        CancellationToken cancellationToken = default);
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

    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;
    private readonly LocalModelDependencyCatalogReview? trustedCatalog;

    public LocalModelDependencyHttpTransfer()
        : this(CreateHardenedHandler(), null, DefaultTimeout, useInjectedCatalog: false)
    {
    }

    internal LocalModelDependencyHttpTransfer(
        HttpMessageHandler handler,
        LocalModelDependencyCatalogReview trustedCatalog,
        TimeSpan? timeout = null)
        : this(
            handler,
            trustedCatalog ?? throw new ArgumentNullException(nameof(trustedCatalog)),
            timeout,
            useInjectedCatalog: true)
    {
    }

    private LocalModelDependencyHttpTransfer(
        HttpMessageHandler handler,
        LocalModelDependencyCatalogReview? trustedCatalog,
        TimeSpan? timeout,
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
    }

    public async Task<LocalModelDependencyTransferResult> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var catalog = trustedCatalog ??
                await new LocalModelDependencyCatalogLoader().LoadEmbeddedAsync(deadline.Token)
                    .ConfigureAwait(false);
            var item = ValidateRequest(catalog, plan, approval, destination);
            using var request = new HttpRequestMessage(HttpMethod.Get, item.SourceUri);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    deadline.Token)
                .ConfigureAwait(false);
            ValidateResponse(response, item);

            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token)
                .ConfigureAwait(false);
            var digest = await CopyAndHashAsync(
                    input,
                    destination,
                    item.ExpectedBytes,
                    deadline.Token)
                .ConfigureAwait(false);
            LocalModelDependencyCatalogLoader.RequireDigest(
                digest,
                item.Sha256,
                "transferred dependency");
            await destination.FlushAsync(deadline.Token).ConfigureAwait(false);
            return new LocalModelDependencyTransferResult(
                plan.PlanSha256,
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
        UseCookies = false,
        UseProxy = false,
    };

    private static LocalModelDependencyTransferItem ValidateRequest(
        LocalModelDependencyCatalogReview trustedCatalog,
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
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
            !DigestEquals(approval.PlanSha256, plan.PlanSha256))
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

    private static void ValidateResponse(
        HttpResponseMessage response,
        LocalModelDependencyTransferItem item)
    {
        if (response.StatusCode != HttpStatusCode.OK ||
            response.RequestMessage?.RequestUri is not { } finalUri ||
            !string.Equals(finalUri.AbsoluteUri, item.SourceUri.AbsoluteUri, StringComparison.Ordinal) ||
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
        CancellationToken cancellationToken)
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
