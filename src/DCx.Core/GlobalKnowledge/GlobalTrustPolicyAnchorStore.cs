using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Security;

namespace DCx.Core.GlobalKnowledge;

public sealed record GlobalTrustPolicyAnchor(
    int SchemaVersion,
    string TrustRootSha256,
    long Revision,
    string PolicySha256);

public interface IGlobalTrustPolicyAnchorStore
{
    Task<GlobalTrustPolicyAnchor?> ReadAsync(
        string trustRootSha256,
        CancellationToken cancellationToken = default);

    Task AdvanceAsync(
        GlobalTrustPolicyAnchor anchor,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsGlobalTrustPolicyAnchorStore : IGlobalTrustPolicyAnchorStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumAnchorCharacters = 2 * 1024;
    private const string TargetPrefix = "DCx/GlobalTrustPolicy/";

    private static readonly object VaultLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly WindowsCredentialVault vault;

    public WindowsGlobalTrustPolicyAnchorStore()
        : this(new WindowsCredentialVault())
    {
    }

    internal WindowsGlobalTrustPolicyAnchorStore(WindowsCredentialVault vault) =>
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));

    public Task<GlobalTrustPolicyAnchor?> ReadAsync(
        string trustRootSha256,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSha256(trustRootSha256, "The global trust-root identity is invalid.");
        var normalized = trustRootSha256.ToLowerInvariant();
        lock (VaultLock)
        {
            return Task.FromResult(ReadCore(normalized));
        }
    }

    public Task AdvanceAsync(
        GlobalTrustPolicyAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnchor(anchor);
        var normalized = anchor with
        {
            TrustRootSha256 = anchor.TrustRootSha256.ToLowerInvariant(),
            PolicySha256 = anchor.PolicySha256.ToLowerInvariant(),
        };
        lock (VaultLock)
        {
            var current = ReadCore(normalized.TrustRootSha256);
            if (current is not null)
            {
                if (normalized.Revision < current.Revision ||
                    normalized.Revision == current.Revision &&
                    !string.Equals(normalized.PolicySha256, current.PolicySha256, StringComparison.Ordinal))
                {
                    throw new GlobalReplicaException("The protected global trust-policy anchor cannot move backward or conflict.");
                }

                if (normalized == current)
                {
                    return Task.CompletedTask;
                }
            }

            try
            {
                vault.Save(TargetName(normalized.TrustRootSha256), JsonSerializer.Serialize(normalized, JsonOptions));
            }
            catch (Win32Exception exception)
            {
                throw new GlobalReplicaException(
                    $"The protected global trust-policy anchor could not be written: {exception.GetType().Name}.");
            }
        }

        return Task.CompletedTask;
    }

    internal static string TargetName(string trustRootSha256) => TargetPrefix + trustRootSha256.ToLowerInvariant();

    private GlobalTrustPolicyAnchor? ReadCore(string trustRootSha256)
    {
        string? value;
        try
        {
            value = vault.Read(TargetName(trustRootSha256));
        }
        catch (Win32Exception exception)
        {
            throw new GlobalReplicaException(
                $"The protected global trust-policy anchor could not be read: {exception.GetType().Name}.");
        }

        if (value is null) return null;
        if (value.Length is <= 0 or > MaximumAnchorCharacters || value.Contains('\0'))
        {
            throw new GlobalReplicaException("The protected global trust-policy anchor is outside its limit.");
        }

        try
        {
            var anchor = JsonSerializer.Deserialize<GlobalTrustPolicyAnchor>(value, JsonOptions)
                ?? throw new GlobalReplicaException("The protected global trust-policy anchor is empty.");
            ValidateAnchor(anchor);
            if (!string.Equals(anchor.TrustRootSha256, trustRootSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new GlobalReplicaException("The protected global trust-policy anchor belongs to another trust root.");
            }

            return anchor with
            {
                TrustRootSha256 = anchor.TrustRootSha256.ToLowerInvariant(),
                PolicySha256 = anchor.PolicySha256.ToLowerInvariant(),
            };
        }
        catch (GlobalReplicaException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new GlobalReplicaException("The protected global trust-policy anchor is malformed.");
        }
    }

    private static void ValidateAnchor(GlobalTrustPolicyAnchor anchor)
    {
        if (anchor.SchemaVersion != CurrentSchemaVersion || anchor.Revision < 1)
        {
            throw new GlobalReplicaException("The protected global trust-policy anchor schema or revision is invalid.");
        }

        ValidateSha256(anchor.TrustRootSha256, "The protected global trust-policy anchor trust root is invalid.");
        ValidateSha256(anchor.PolicySha256, "The protected global trust-policy anchor digest is invalid.");
    }

    private static void ValidateSha256(string value, string message)
    {
        if (value is not { Length: 64 } || !value.All(char.IsAsciiHexDigit))
        {
            throw new GlobalReplicaException(message);
        }
    }
}
