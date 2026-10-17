using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Security;

namespace DCx.Core.Finance;

public sealed record PayoutRecipientCandidateReview(
    string ContributorPseudonym,
    string ExactDestination,
    string DestinationSha256);

public sealed record PayoutRecipientSnapshotSummary(
    string SnapshotId,
    string ContributorPseudonym,
    string DestinationSha256,
    DateTimeOffset ApprovedUtc,
    DateTimeOffset? RevokedUtc)
{
    public bool IsActive => RevokedUtc is null;

    public PayoutRecipientApproval Approval =>
        new(SnapshotId, DestinationSha256);
}

internal interface IPayoutRecipientSecretStore
{
    void Save(string targetName, string value);
    string? Read(string targetName);
    bool Delete(string targetName);
}

internal sealed class WindowsPayoutRecipientSecretStore : IPayoutRecipientSecretStore
{
    private readonly WindowsCredentialVault vault = new();

    public void Save(string targetName, string value) => vault.Save(targetName, value);
    public string? Read(string targetName) => vault.Read(targetName);
    public bool Delete(string targetName) => vault.Delete(targetName);
}

public sealed class PayoutRecipientSnapshotStore : IPayPalSandboxRecipientDirectory
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumSnapshots = 1_000;
    public const int MaximumStateBytes = 512 * 1024;
    internal const string CredentialTargetPrefix = "DCx/PayPalSandboxRecipient/";

    private const int MaximumProtectedSecretCharacters = 4 * 1024;
    private const string PendingState = "pending";
    private const string ActiveState = "active";
    private const string RevokedState = "revoked";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };

    private static readonly JsonSerializerOptions ProtectedJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly string directoryRoot;
    private readonly string statePath;
    private readonly IPayoutRecipientSecretStore secretStore;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private PayoutRecipientSnapshotStore(
        string directoryRoot,
        IPayoutRecipientSecretStore secretStore)
    {
        this.directoryRoot = directoryRoot;
        this.secretStore = secretStore;
        statePath = Path.Combine(directoryRoot, "snapshots.json");
    }

    public static Task<PayoutRecipientSnapshotStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default) =>
        OpenAsync(applicationDataRoot, new WindowsPayoutRecipientSecretStore(), cancellationToken);

    internal static async Task<PayoutRecipientSnapshotStore> OpenAsync(
        string applicationDataRoot,
        IPayoutRecipientSecretStore secretStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        var applicationRoot = ValidateRoot(applicationDataRoot);
        var directoryRoot = Path.Combine(applicationRoot, "finance", "payout-recipients");
        Directory.CreateDirectory(directoryRoot);
        RejectReparsePoint(
            directoryRoot,
            "The payout-recipient directory cannot be a reparse point.");
        var store = new PayoutRecipientSnapshotStore(directoryRoot, secretStore);
        store.ValidateStatePathIfPresent();
        await store.RecoverInterruptedWritesAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public static PayoutRecipientCandidateReview ReviewCandidate(
        string contributorPseudonym,
        string exactDestination)
    {
        PayPalSandboxValidation.RequireContributorPseudonym(contributorPseudonym);
        if (!PayPalRecipientAddress.IsValid(exactDestination))
        {
            throw new InvalidOperationException("The payout recipient destination is not a supported email address.");
        }

        return new PayoutRecipientCandidateReview(
            contributorPseudonym,
            exactDestination,
            PayPalRecipientAddress.DestinationSha256(exactDestination));
    }

    public async Task<PayoutRecipientSnapshotSummary> ApproveAsync(
        string contributorPseudonym,
        string exactDestination,
        bool explicitlyApproved,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException("Saving a payout recipient snapshot requires explicit owner approval.");
        }

        var review = ReviewCandidate(contributorPseudonym, exactDestination);
        var approvedUtc = RequireUtc(now, nameof(now));
        var snapshotId = $"recipient:{Guid.NewGuid():N}";
        var targetName = TargetName(snapshotId);
        var pending = new StoredSnapshot(
            snapshotId,
            review.ContributorPseudonym,
            review.DestinationSha256,
            targetName,
            PendingState,
            approvedUtc,
            null,
            null);

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (state.Snapshots.Count >= MaximumSnapshots)
            {
                throw new InvalidOperationException("The payout recipient snapshot limit was reached.");
            }

            if (state.Snapshots.Any(item => item.State == ActiveState &&
                    (string.Equals(
                         item.ContributorPseudonym,
                         review.ContributorPseudonym,
                         StringComparison.Ordinal) ||
                     string.Equals(
                         item.DestinationSha256,
                         review.DestinationSha256,
                         StringComparison.Ordinal))))
            {
                throw new InvalidOperationException(
                    "An active payout recipient snapshot already uses this contributor or destination.");
            }

            await WriteUnsafeAsync(
                    state with { Snapshots = [.. state.Snapshots, pending] },
                    cancellationToken)
                .ConfigureAwait(false);

            var protectedRecord = new ProtectedSnapshot(
                CurrentSchemaVersion,
                snapshotId,
                review.ContributorPseudonym,
                review.ExactDestination,
                review.DestinationSha256,
                approvedUtc);
            var protectedJson = JsonSerializer.Serialize(protectedRecord, ProtectedJsonOptions);
            if (protectedJson.Length is <= 0 or > MaximumProtectedSecretCharacters)
            {
                await RemovePendingUnsafeAsync(snapshotId, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("The protected payout recipient snapshot exceeds its size limit.");
            }

            try
            {
                secretStore.Save(targetName, protectedJson);
                var current = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
                var index = FindSnapshotIndex(current, snapshotId, PendingState);
                var active = pending with { State = ActiveState, ApprovedUtc = approvedUtc };
                var snapshots = current.Snapshots.ToArray();
                snapshots[index] = active;
                await WriteUnsafeAsync(current with { Snapshots = snapshots }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                TryDeleteProtectedSecret(targetName);
                await RemovePendingUnsafeAsync(snapshotId, CancellationToken.None).ConfigureAwait(false);
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new InvalidOperationException(
                    $"The protected payout recipient snapshot could not be saved: {exception.GetType().Name}.");
            }

            return ToSummary(pending with { State = ActiveState, ApprovedUtc = approvedUtc });
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<IReadOnlyList<PayoutRecipientSnapshotSummary>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return state.Snapshots
                .Where(item => item.State is ActiveState or RevokedState)
                .OrderByDescending(item => item.CreatedUtc)
                .ThenBy(item => item.SnapshotId, StringComparer.Ordinal)
                .Select(ToSummary)
                .ToArray();
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<PayoutRecipientApproval?> GetOwnerApprovedApprovalAsync(
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        return resolved is null
            ? null
            : new PayoutRecipientApproval(
                resolved.SnapshotId,
                PayPalRecipientAddress.DestinationSha256(resolved.Email));
    }

    public async Task RevokeAsync(
        string snapshotId,
        bool explicitlyApproved,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException("Revoking a payout recipient snapshot requires explicit owner approval.");
        }

        PayPalSandboxValidation.RequireRecipientSnapshotId(snapshotId);
        var revokedUtc = RequireUtc(now, nameof(now));
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = FindSnapshotIndex(state, snapshotId, ActiveState);
            var current = state.Snapshots[index];
            if (revokedUtc < current.ApprovedUtc)
            {
                throw new InvalidOperationException("A payout recipient snapshot cannot be revoked before approval.");
            }

            var snapshots = state.Snapshots.ToArray();
            snapshots[index] = current with { State = RevokedState, RevokedUtc = revokedUtc };
            await WriteUnsafeAsync(state with { Snapshots = snapshots }, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                secretStore.Delete(current.CredentialTarget);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"The revoked payout recipient secret could not be removed: {exception.GetType().Name}.");
            }
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async ValueTask<PayPalSandboxRecipient?> ResolveAsync(
        string snapshotId,
        CancellationToken cancellationToken)
    {
        PayPalSandboxValidation.RequireRecipientSnapshotId(snapshotId);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var stored = state.Snapshots.SingleOrDefault(item =>
                item.State == ActiveState &&
                string.Equals(item.SnapshotId, snapshotId, StringComparison.Ordinal));
            if (stored is null) return null;

            var secret = ReadProtectedSecret(stored.CredentialTarget);
            if (secret is null)
            {
                throw new InvalidOperationException("The protected payout recipient secret is missing.");
            }

            ProtectedSnapshot protectedSnapshot;
            try
            {
                protectedSnapshot = JsonSerializer.Deserialize<ProtectedSnapshot>(secret, ProtectedJsonOptions)
                    ?? throw new InvalidOperationException("The protected payout recipient snapshot is empty.");
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The protected payout recipient snapshot is malformed.");
            }

            ValidateProtectedBinding(stored, protectedSnapshot);
            return new PayPalSandboxRecipient(
                stored.SnapshotId,
                stored.ContributorPseudonym,
                protectedSnapshot.ExactDestination,
                IsVerifiedAndEligible: false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    internal string StatePathForTests => statePath;

    private async Task RecoverInterruptedWritesAsync(CancellationToken cancellationToken)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var pending = state.Snapshots.Where(item => item.State == PendingState).ToArray();
            foreach (var item in state.Snapshots.Where(item => item.State == RevokedState))
            {
                TryDeleteProtectedSecret(item.CredentialTarget);
            }

            if (pending.Length == 0) return;
            foreach (var item in pending)
            {
                TryDeleteProtectedSecret(item.CredentialTarget);
            }

            await WriteUnsafeAsync(
                    state with
                    {
                        Snapshots = state.Snapshots
                            .Where(item => item.State != PendingState)
                            .ToArray(),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private async Task RemovePendingUnsafeAsync(
        string snapshotId,
        CancellationToken cancellationToken)
    {
        var state = await ReadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        if (!state.Snapshots.Any(item => item.State == PendingState && item.SnapshotId == snapshotId)) return;
        await WriteUnsafeAsync(
                state with
                {
                    Snapshots = state.Snapshots
                        .Where(item => !(item.State == PendingState && item.SnapshotId == snapshotId))
                        .ToArray(),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StoredState> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        ValidateStatePathIfPresent();
        if (!File.Exists(statePath)) return new StoredState(CurrentSchemaVersion, []);
        var file = new FileInfo(statePath);
        if (file.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The payout recipient snapshot state is outside its size limit.");
        }

        try
        {
            await using var stream = new FileStream(
                statePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync<StoredState>(
                    stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The payout recipient snapshot state is empty.");
            ValidateState(state);
            return state;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The payout recipient snapshot state is malformed.");
        }
    }

    private async Task WriteUnsafeAsync(StoredState state, CancellationToken cancellationToken)
    {
        ValidateState(state);
        var content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        if (content.Length is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The payout recipient snapshot state exceeds its size limit.");
        }

        RejectReparsePoint(directoryRoot, "The payout-recipient directory cannot be a reparse point.");
        ValidateStatePathIfPresent();
        var temporary = Path.Combine(directoryRoot, $".snapshots.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ValidateStatePathIfPresent();
            File.Move(temporary, statePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateState(StoredState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion || state.Snapshots is null ||
            state.Snapshots.Count > MaximumSnapshots)
        {
            throw new InvalidOperationException("The payout recipient snapshot state schema is unsupported.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in state.Snapshots)
        {
            PayPalSandboxValidation.RequireRecipientSnapshotId(item.SnapshotId);
            PayPalSandboxValidation.RequireContributorPseudonym(item.ContributorPseudonym);
            if (!PayoutContractValidation.IsLowerSha256(item.DestinationSha256) ||
                item.CredentialTarget != TargetName(item.SnapshotId) ||
                item.State is not (PendingState or ActiveState or RevokedState) ||
                item.CreatedUtc == default || item.CreatedUtc.Offset != TimeSpan.Zero ||
                item.ApprovedUtc is { Offset: var approvedOffset } && approvedOffset != TimeSpan.Zero ||
                item.RevokedUtc is { Offset: var revokedOffset } && revokedOffset != TimeSpan.Zero ||
                item.State == PendingState && (item.ApprovedUtc is not null || item.RevokedUtc is not null) ||
                item.State == ActiveState && (item.ApprovedUtc is null || item.RevokedUtc is not null) ||
                item.State == RevokedState && (item.ApprovedUtc is null || item.RevokedUtc is null) ||
                item.ApprovedUtc is { } approved && approved < item.CreatedUtc ||
                item.RevokedUtc is { } revoked && item.ApprovedUtc is { } approvedForRevocation &&
                    revoked < approvedForRevocation ||
                !ids.Add(item.SnapshotId) || !targets.Add(item.CredentialTarget))
            {
                throw new InvalidOperationException("A stored payout recipient snapshot is invalid or duplicated.");
            }
        }

        var active = state.Snapshots.Where(item => item.State == ActiveState).ToArray();
        if (active.Select(item => item.ContributorPseudonym).Distinct(StringComparer.Ordinal).Count() != active.Length ||
            active.Select(item => item.DestinationSha256).Distinct(StringComparer.Ordinal).Count() != active.Length)
        {
            throw new InvalidOperationException("Active payout recipient snapshots conflict.");
        }
    }

    private static void ValidateProtectedBinding(
        StoredSnapshot stored,
        ProtectedSnapshot protectedSnapshot)
    {
        if (protectedSnapshot.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(protectedSnapshot.SnapshotId, stored.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(
                protectedSnapshot.ContributorPseudonym,
                stored.ContributorPseudonym,
                StringComparison.Ordinal) ||
            !string.Equals(
                protectedSnapshot.DestinationSha256,
                stored.DestinationSha256,
                StringComparison.Ordinal) ||
            protectedSnapshot.ApprovedUtc != stored.ApprovedUtc ||
            !PayPalRecipientAddress.IsValid(protectedSnapshot.ExactDestination) ||
            !string.Equals(
                PayPalRecipientAddress.DestinationSha256(protectedSnapshot.ExactDestination),
                stored.DestinationSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The protected payout recipient snapshot binding is invalid.");
        }
    }

    private string? ReadProtectedSecret(string targetName)
    {
        try
        {
            var value = secretStore.Read(targetName);
            if (value is null) return null;
            if (value.Length is <= 0 or > MaximumProtectedSecretCharacters || value.Contains('\0'))
            {
                throw new InvalidOperationException("The protected payout recipient snapshot is outside its size limit.");
            }

            return value;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The protected payout recipient snapshot could not be read: {exception.GetType().Name}.");
        }
    }

    private void TryDeleteProtectedSecret(string targetName)
    {
        try
        {
            secretStore.Delete(targetName);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"The protected payout recipient snapshot could not be cleaned up: {exception.GetType().Name}.");
        }
    }

    private static int FindSnapshotIndex(StoredState state, string snapshotId, string requiredState)
    {
        for (var index = 0; index < state.Snapshots.Count; index++)
        {
            var item = state.Snapshots[index];
            if (string.Equals(item.SnapshotId, snapshotId, StringComparison.Ordinal) &&
                item.State == requiredState)
            {
                return index;
            }
        }

        throw new InvalidOperationException("The payout recipient snapshot is missing or not in the required state.");
    }

    private static PayoutRecipientSnapshotSummary ToSummary(StoredSnapshot stored) =>
        new(
            stored.SnapshotId,
            stored.ContributorPseudonym,
            stored.DestinationSha256,
            stored.ApprovedUtc ?? throw new InvalidOperationException(
                "The payout recipient snapshot has no approval time."),
            stored.RevokedUtc);

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A nondefault UTC time is required.", parameterName);
        }

        return value;
    }

    internal static string TargetName(string snapshotId) =>
        CredentialTargetPrefix + snapshotId;

    private static string ValidateRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var canonical = Path.GetFullPath(root);
        var pathRoot = Path.GetPathRoot(canonical);
        if (pathRoot is not null && canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The payout recipient application-data root cannot be a filesystem root.");
        }

        Directory.CreateDirectory(canonical);
        RejectReparsePoint(canonical, "The payout recipient application-data root cannot be a reparse point.");
        return canonical;
    }

    private void ValidateStatePathIfPresent()
    {
        RejectReparsePoint(directoryRoot, "The payout-recipient directory cannot be a reparse point.");
        var entries = Directory.EnumerateFileSystemEntries(directoryRoot)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(statePath),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length == 0) return;
        if (entries.Length != 1 ||
            !string.Equals(Path.GetFileName(entries[0]), Path.GetFileName(statePath), StringComparison.Ordinal) ||
            (File.GetAttributes(entries[0]) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException("The payout recipient snapshot state path is unsafe.");
        }
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record StoredState(
        int SchemaVersion,
        IReadOnlyList<StoredSnapshot> Snapshots);

    private sealed record StoredSnapshot(
        string SnapshotId,
        string ContributorPseudonym,
        string DestinationSha256,
        string CredentialTarget,
        string State,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? ApprovedUtc,
        DateTimeOffset? RevokedUtc);

    private sealed record ProtectedSnapshot(
        int SchemaVersion,
        string SnapshotId,
        string ContributorPseudonym,
        string ExactDestination,
        string DestinationSha256,
        DateTimeOffset ApprovedUtc);
}

internal static class PayPalRecipientAddress
{
    public static bool IsValid(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 127 ||
            value.Any(character => character is <= ' ' or > '~')) return false;
        var at = value.IndexOf('@');
        if (at is < 1 || at != value.LastIndexOf('@') || at > 64 || at == value.Length - 1) return false;
        var local = value.AsSpan(0, at);
        var domain = value.AsSpan(at + 1);
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal) ||
            !local.ToString().All(character => char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '%' or '+' or '-')) return false;
        if (domain[0] == '.' || domain[^1] == '.' || !domain.Contains('.')) return false;
        return domain.ToString().Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    public static string DestinationSha256(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
