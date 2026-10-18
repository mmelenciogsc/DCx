using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.LocalModels;

public sealed record LocalModelDependencyTransferSetApproval(
    int SchemaVersion,
    string PlanSha256,
    string RedirectPolicySha256,
    bool Approved)
{
    public const int CurrentSchemaVersion = 2;

    public static LocalModelDependencyTransferSetApproval Create(
        LocalModelDependencyTransferPlan plan,
        bool approved) => Create(
            plan,
            LocalModelDependencyRedirectPolicyReview.DenyAll,
            approved);

    public static LocalModelDependencyTransferSetApproval Create(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyRedirectPolicyReview redirectPolicy,
        bool approved)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        LocalModelDependencyRedirectPolicyReview.Validate(redirectPolicy);
        return new LocalModelDependencyTransferSetApproval(
            CurrentSchemaVersion,
            plan.PlanSha256,
            redirectPolicy.PolicySha256,
            approved);
    }
}

public sealed record LocalModelDependencyTransferredFile(
    int Index,
    string FileName,
    long Bytes,
    string Sha256);

public sealed record LocalModelDependencyTransferSetReceipt(
    int SchemaVersion,
    string CatalogId,
    string CatalogSha256,
    string PlanSha256,
    string RedirectPolicySha256,
    long TransferBytes,
    IReadOnlyList<LocalModelDependencyTransferredFile> Files);

public enum LocalModelDependencyTransferSetStage
{
    Preparing,
    Transferring,
    ProtectingReceipt,
    Published,
    Verifying,
    Completed,
}

public sealed record LocalModelDependencyTransferSetProgress(
    LocalModelDependencyTransferSetStage Stage,
    int ItemNumber,
    int ItemCount,
    long ItemBytesTransferred,
    long ItemExpectedBytes,
    long TotalBytesTransferred,
    long TotalExpectedBytes);

public interface ILocalModelDependencyTransferSetCoordinator : IDisposable
{
    LocalModelDependencyRedirectPolicyReview RedirectPolicyReview { get; }

    Task<LocalModelDependencyTransferSetReceipt> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferSetApproval approval,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyTransferSetProgress>? progress = null);

    Task<LocalModelDependencyTransferSetReceipt> VerifyAsync(
        LocalModelDependencyTransferPlan plan,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyTransferSetProgress>? progress = null);

    Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates an explicitly approved plan into a private, inert and atomically published set.
/// It does not extract, install, register, select or execute transferred content.
/// </summary>
public sealed class LocalModelDependencyTransferSetCoordinator : ILocalModelDependencyTransferSetCoordinator
{
    public const int CurrentReceiptSchemaVersion = 2;
    public const int MaximumReceiptBytes = 64 * 1024;
    internal const string AnchorStateKind = "dependency-transfer-set";
    private const string ReceiptFileName = "receipt.json";
    private const string IncomingPrefix = ".incoming-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly string applicationDataRoot;
    private readonly ILocalModelDependencyTransfer transfer;
    private readonly ILocalModelApprovalStateAnchorStore anchors;
    private readonly Func<string, long?> availableFreeBytes;
    private readonly bool ownsTransfer;
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public LocalModelDependencyRedirectPolicyReview RedirectPolicyReview => transfer.RedirectPolicyReview;

    public LocalModelDependencyTransferSetCoordinator(string applicationDataRoot)
        : this(
            applicationDataRoot,
            new LocalModelDependencyHttpTransfer(),
            new WindowsLocalModelApprovalStateAnchorStore(),
            LocalModelDependencyTransferPlanner.TryGetAvailableFreeBytes,
            ownsTransfer: true)
    {
    }

    public LocalModelDependencyTransferSetCoordinator(
        string applicationDataRoot,
        LocalModelDependencyRedirectReviewSession redirectReviewSession)
        : this(
            applicationDataRoot,
            new LocalModelDependencyHttpTransfer(
                RequireCurrentSession(redirectReviewSession).CreatePolicy()),
            new WindowsLocalModelApprovalStateAnchorStore(),
            LocalModelDependencyTransferPlanner.TryGetAvailableFreeBytes,
            ownsTransfer: true)
    {
    }

    private static LocalModelDependencyRedirectReviewSession RequireCurrentSession(
        LocalModelDependencyRedirectReviewSession? session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.IsCurrent)
        {
            throw new ArgumentException("The dependency redirect review session has expired.", nameof(session));
        }
        return session;
    }

    internal LocalModelDependencyTransferSetCoordinator(
        string applicationDataRoot,
        ILocalModelDependencyTransfer transfer,
        ILocalModelApprovalStateAnchorStore anchors,
        Func<string, long?> availableFreeBytes,
        bool ownsTransfer = false)
    {
        this.applicationDataRoot = Path.GetFullPath(
            applicationDataRoot ?? throw new ArgumentNullException(nameof(applicationDataRoot)));
        this.transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        LocalModelDependencyRedirectPolicyReview.Validate(this.transfer.RedirectPolicyReview);
        this.anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        this.availableFreeBytes = availableFreeBytes ?? throw new ArgumentNullException(nameof(availableFreeBytes));
        this.ownsTransfer = ownsTransfer;
    }

    public async Task<LocalModelDependencyTransferSetReceipt> TransferAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferSetApproval approval,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyTransferSetProgress>? progress = null)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TransferCoreAsync(plan, approval, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<LocalModelDependencyTransferSetReceipt> TransferCoreAsync(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferSetApproval approval,
        CancellationToken cancellationToken,
        IProgress<LocalModelDependencyTransferSetProgress>? progress)
    {
        var approvedRedirectPolicy = transfer.RedirectPolicyReview;
        LocalModelDependencyRedirectPolicyReview.Validate(approvedRedirectPolicy);
        ValidateApproval(plan, approval, approvedRedirectPolicy);
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, LocalModelDependencyTransferSetStage.Preparing, plan, 0, 0, 0, 0);
        var appRoot = LocalModelStoragePathGuard.RequireDirectory(
            applicationDataRoot,
            "dependency transfer application root");
        var observed = availableFreeBytes(appRoot);
        if (observed is null || observed < plan.MinimumFreeBytes)
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_capacity_unapproved");
        }
        var currentPlan = plan with
        {
            ObservedAvailableFreeBytes = observed,
            CapacitySufficient = true,
        };
        LocalModelDependencyTransferPlanner.ValidateForTransfer(currentPlan);

        var transfersRoot = EnsureTransfersRoot(appRoot);
        transfersRoot = LocalModelStoragePathGuard.RequireDirectory(
            transfersRoot,
            "dependency transfer root");
        var destination = Path.Combine(transfersRoot, currentPlan.PlanSha256);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_destination_exists");
        }

        var incoming = Path.Combine(transfersRoot, IncomingPrefix + Guid.NewGuid().ToString("N"));
        LocalModelStoragePathGuard.EnsureDirectory(incoming, "dependency transfer incoming");
        try
        {
            var files = new List<LocalModelDependencyTransferredFile>(currentPlan.Items.Count);
            long completedBytes = 0;
            for (var index = 0; index < currentPlan.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureCurrentRedirectPolicy(approvedRedirectPolicy.PolicySha256);
                var item = currentPlan.Items[index];
                Report(
                    progress,
                    LocalModelDependencyTransferSetStage.Transferring,
                    currentPlan,
                    index + 1,
                    0,
                    item.ExpectedBytes,
                    completedBytes);
                var fileName = FileName(index, item.Sha256);
                var path = LocalModelStoragePathGuard.ValidateFile(
                    Path.Combine(incoming, fileName),
                    mustExist: false,
                    "dependency transfer payload");
                await using (var output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
                {
                    var result = await transfer.TransferAsync(
                        currentPlan,
                            LocalModelDependencyTransferApproval.Create(
                                currentPlan,
                                item,
                                approvedRedirectPolicy,
                                approved: true),
                            output,
                            cancellationToken,
                            new InlineProgress<LocalModelDependencyItemTransferProgress>(itemProgress => Report(
                                progress,
                                LocalModelDependencyTransferSetStage.Transferring,
                                currentPlan,
                                index + 1,
                                itemProgress.BytesTransferred,
                                itemProgress.ExpectedBytes,
                                completedBytes + itemProgress.BytesTransferred)))
                        .ConfigureAwait(false);
                    if (result.Bytes != item.ExpectedBytes ||
                        !string.Equals(result.SourceUri.AbsoluteUri, item.SourceUri.AbsoluteUri, StringComparison.Ordinal) ||
                        !DigestEquals(result.PlanSha256, currentPlan.PlanSha256) ||
                        !DigestEquals(result.RedirectPolicySha256, approvedRedirectPolicy.PolicySha256) ||
                        !DigestEquals(result.Sha256, item.Sha256))
                    {
                        throw new LocalModelDependencyTransferException("dependency_transfer_result_invalid");
                    }
                    EnsureCurrentRedirectPolicy(approvedRedirectPolicy.PolicySha256);
                }
                LocalModelStoragePathGuard.ValidateFile(path, mustExist: true, "dependency transfer payload");
                files.Add(new LocalModelDependencyTransferredFile(
                    index,
                    fileName,
                    item.ExpectedBytes,
                    item.Sha256));
                completedBytes = checked(completedBytes + item.ExpectedBytes);
            }

            Report(
                progress,
                LocalModelDependencyTransferSetStage.ProtectingReceipt,
                currentPlan,
                currentPlan.Items.Count,
                0,
                0,
                currentPlan.TransferBytes);
            var receipt = new LocalModelDependencyTransferSetReceipt(
                CurrentReceiptSchemaVersion,
                currentPlan.CatalogId,
                currentPlan.CatalogSha256,
                currentPlan.PlanSha256,
                approvedRedirectPolicy.PolicySha256,
                currentPlan.TransferBytes,
                Array.AsReadOnly(files.ToArray()));
            var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions);
            if (receiptBytes.Length is < 1 or > MaximumReceiptBytes)
            {
                throw new InvalidOperationException("The dependency transfer receipt is outside its size limit.");
            }
            var receiptPath = LocalModelStoragePathGuard.ValidateFile(
                Path.Combine(incoming, ReceiptFileName),
                mustExist: false,
                "dependency transfer receipt");
            try
            {
                await using (var receiptStream = new FileStream(
                    receiptPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await receiptStream.WriteAsync(receiptBytes, cancellationToken).ConfigureAwait(false);
                    await receiptStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                var receiptSha256 = Convert.ToHexStringLower(SHA256.HashData(receiptBytes));
                await anchors.WriteAsync(
                        new LocalModelApprovalStateAnchor(
                            WindowsLocalModelApprovalStateAnchorStore.CurrentSchemaVersion,
                            AnchorStateKind,
                            currentPlan.PlanSha256,
                            receiptSha256,
                            receiptBytes.LongLength),
                        cancellationToken)
                    .ConfigureAwait(false);

                LocalModelStoragePathGuard.RequireDirectory(incoming, "dependency transfer incoming");
                if (Directory.Exists(destination) || File.Exists(destination))
                {
                    throw new LocalModelDependencyTransferException("dependency_transfer_destination_exists");
                }
                Directory.Move(incoming, destination);
                LocalModelStoragePathGuard.RequireDirectory(destination, "dependency transfer completed set");
                ReportAfterCommit(
                    progress,
                    LocalModelDependencyTransferSetStage.Published,
                    currentPlan,
                    currentPlan.Items.Count,
                    0,
                    0,
                    currentPlan.TransferBytes);
                return receipt;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(receiptBytes);
            }
        }
        catch
        {
            LocalModelStoragePathGuard.TryDeleteTemporaryDirectory(
                transfersRoot,
                incoming,
                "dependency transfer incoming");
            throw;
        }
    }

    public async Task<LocalModelDependencyTransferSetReceipt> VerifyAsync(
        LocalModelDependencyTransferPlan plan,
        CancellationToken cancellationToken = default,
        IProgress<LocalModelDependencyTransferSetProgress>? progress = null)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await VerifyCoreAsync(plan, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<LocalModelDependencyTransferSetReceipt> VerifyCoreAsync(
        LocalModelDependencyTransferPlan plan,
        CancellationToken cancellationToken,
        IProgress<LocalModelDependencyTransferSetProgress>? progress)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, LocalModelDependencyTransferSetStage.Verifying, plan, 0, 0, 0, 0);
        var appRoot = LocalModelStoragePathGuard.RequireDirectory(
            applicationDataRoot,
            "dependency transfer application root");
        var transfersRoot = LocalModelStoragePathGuard.RequireDirectory(
            Path.Combine(appRoot, "LocalModels", "dependency-transfers"),
            "dependency transfer root");
        var completed = LocalModelStoragePathGuard.RequireDirectory(
            Path.Combine(transfersRoot, plan.PlanSha256),
            "dependency transfer completed set");
        var receiptPath = LocalModelStoragePathGuard.ValidateFile(
            Path.Combine(completed, ReceiptFileName),
            mustExist: true,
            "dependency transfer receipt");
        var receiptInfo = new FileInfo(receiptPath);
        if (receiptInfo.Length is < 1 or > MaximumReceiptBytes)
        {
            throw new InvalidOperationException("The dependency transfer receipt is outside its size limit.");
        }
        var receiptBytes = await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        try
        {
            if (receiptBytes.LongLength != receiptInfo.Length)
            {
                throw new InvalidOperationException("The dependency transfer receipt changed during verification.");
            }
            var receipt = ParseReceipt(receiptBytes);
            ValidateReceipt(receipt, plan);
            var receiptSha256 = Convert.ToHexStringLower(SHA256.HashData(receiptBytes));
            var anchor = await anchors.ReadAsync(AnchorStateKind, plan.PlanSha256, cancellationToken)
                .ConfigureAwait(false);
            if (anchor is null || anchor.StateBytes != receiptBytes.LongLength ||
                !DigestEquals(anchor.StateSha256, receiptSha256))
            {
                throw new InvalidOperationException("The protected dependency transfer receipt does not match.");
            }

            var expectedNames = receipt.Files.Select(file => file.FileName)
                .Append(ReceiptFileName)
                .ToHashSet(StringComparer.Ordinal);
            var actualNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Directory.EnumerateFileSystemEntries(completed))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = LocalModelStoragePathGuard.ValidateFile(
                    entry,
                    mustExist: true,
                    "dependency transfer completed file");
                if (!actualNames.Add(Path.GetFileName(file)))
                {
                    throw new InvalidOperationException("The dependency transfer completed set has duplicate files.");
                }
            }
            if (!actualNames.SetEquals(expectedNames))
            {
                throw new InvalidOperationException("The dependency transfer completed set inventory does not match.");
            }

            foreach (var file in receipt.Files)
            {
                var path = LocalModelStoragePathGuard.ValidateFile(
                    Path.Combine(completed, file.FileName),
                    mustExist: true,
                    "dependency transfer completed payload");
                var info = new FileInfo(path);
                if (info.Length != file.Bytes)
                {
                    throw new InvalidOperationException("The dependency transfer completed payload length does not match.");
                }
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false));
                if (!DigestEquals(digest, file.Sha256))
                {
                    throw new InvalidOperationException("The dependency transfer completed payload digest does not match.");
                }
                Report(
                    progress,
                    LocalModelDependencyTransferSetStage.Verifying,
                    plan,
                    file.Index + 1,
                    file.Bytes,
                    file.Bytes,
                    receipt.Files.Take(file.Index + 1).Sum(value => value.Bytes));
            }
            ReportAfterCommit(
                progress,
                LocalModelDependencyTransferSetStage.Completed,
                plan,
                receipt.Files.Count,
                0,
                0,
                plan.TransferBytes);
            return receipt;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(receiptBytes);
        }
    }

    public async Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var appRoot = LocalModelStoragePathGuard.RequireDirectory(
                applicationDataRoot,
                "dependency transfer application root");
            var transfersRoot = EnsureTransfersRoot(appRoot);
            var removed = 0;
            foreach (var entry in Directory.EnumerateDirectories(transfersRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (!IsIncomingName(name)) continue;
                LocalModelStoragePathGuard.TryDeleteTemporaryDirectory(
                    transfersRoot,
                    entry,
                    "dependency transfer abandoned incoming");
                if (!Directory.Exists(entry)) removed++;
            }
            return removed;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (ownsTransfer && transfer is IDisposable disposable) disposable.Dispose();
    }

    private string EnsureTransfersRoot(string appRoot)
    {
        var localModels = LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(appRoot, "LocalModels"),
            "local-model storage");
        return LocalModelStoragePathGuard.EnsureDirectory(
            Path.Combine(localModels, "dependency-transfers"),
            "dependency transfer root");
    }

    private void ValidateApproval(
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferSetApproval approval,
        LocalModelDependencyRedirectPolicyReview redirectPolicy)
    {
        LocalModelDependencyTransferPlanner.ValidateForTransfer(plan);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.SchemaVersion != LocalModelDependencyTransferSetApproval.CurrentSchemaVersion ||
            !approval.Approved ||
            (transfer.RedirectPolicyPlanSha256 is { } reviewedPlanSha256 &&
                !DigestEquals(reviewedPlanSha256, plan.PlanSha256)) ||
            !DigestEquals(approval.PlanSha256, plan.PlanSha256) ||
            !DigestEquals(approval.RedirectPolicySha256, redirectPolicy.PolicySha256))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
        }
    }

    private void EnsureCurrentRedirectPolicy(string expectedSha256)
    {
        var current = transfer.RedirectPolicyReview;
        LocalModelDependencyRedirectPolicyReview.Validate(current);
        if (!DigestEquals(current.PolicySha256, expectedSha256))
        {
            throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
        }
    }

    private static LocalModelDependencyTransferSetReceipt ParseReceipt(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            RejectDuplicateProperties(document.RootElement);
            return JsonSerializer.Deserialize<LocalModelDependencyTransferSetReceipt>(bytes, JsonOptions)
                ?? throw new InvalidOperationException("The dependency transfer receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The dependency transfer receipt is malformed.", exception);
        }
    }

    private static void ValidateReceipt(
        LocalModelDependencyTransferSetReceipt receipt,
        LocalModelDependencyTransferPlan plan)
    {
        if (receipt.SchemaVersion != CurrentReceiptSchemaVersion ||
            receipt.TransferBytes != plan.TransferBytes ||
            !string.Equals(receipt.CatalogId, plan.CatalogId, StringComparison.Ordinal) ||
            !DigestEquals(receipt.CatalogSha256, plan.CatalogSha256) ||
            !DigestEquals(receipt.PlanSha256, plan.PlanSha256) ||
            !IsSha256(receipt.RedirectPolicySha256) ||
            receipt.Files is null || receipt.Files.Count != plan.Items.Count)
        {
            throw new InvalidOperationException("The dependency transfer receipt identity does not match.");
        }
        for (var index = 0; index < plan.Items.Count; index++)
        {
            var file = receipt.Files[index];
            var item = plan.Items[index];
            if (file is null || file.Index != index || file.FileName != FileName(index, item.Sha256) ||
                file.Bytes != item.ExpectedBytes || !DigestEquals(file.Sha256, item.Sha256))
            {
                throw new InvalidOperationException("The dependency transfer receipt file inventory does not match.");
            }
        }
    }

    private static string FileName(int index, string sha256) => $"{index:D2}-{sha256}.payload";

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Report(
        IProgress<LocalModelDependencyTransferSetProgress>? progress,
        LocalModelDependencyTransferSetStage stage,
        LocalModelDependencyTransferPlan plan,
        int itemNumber,
        long itemBytesTransferred,
        long itemExpectedBytes,
        long totalBytesTransferred) =>
        progress?.Report(new LocalModelDependencyTransferSetProgress(
            stage,
            itemNumber,
            plan.Items.Count,
            itemBytesTransferred,
            itemExpectedBytes,
            totalBytesTransferred,
            plan.TransferBytes));

    private static void ReportAfterCommit(
        IProgress<LocalModelDependencyTransferSetProgress>? progress,
        LocalModelDependencyTransferSetStage stage,
        LocalModelDependencyTransferPlan plan,
        int itemNumber,
        long itemBytesTransferred,
        long itemExpectedBytes,
        long totalBytesTransferred)
    {
        try
        {
            Report(
                progress,
                stage,
                plan,
                itemNumber,
                itemBytesTransferred,
                itemExpectedBytes,
                totalBytesTransferred);
        }
        catch (Exception)
        {
            // A non-authoritative observer cannot convert a durable publication or completed
            // verification into a reported operation failure after the commit boundary.
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static bool IsIncomingName(string name) =>
        name.Length == IncomingPrefix.Length + 32 &&
        name.StartsWith(IncomingPrefix, StringComparison.Ordinal) &&
        name.AsSpan(IncomingPrefix.Length).ToString().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool DigestEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftBytes = System.Text.Encoding.ASCII.GetBytes(left);
        var rightBytes = System.Text.Encoding.ASCII.GetBytes(right);
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

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException("The dependency transfer receipt has duplicate properties.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }
}
