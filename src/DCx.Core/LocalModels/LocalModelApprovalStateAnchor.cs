using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Security;

namespace DCx.Core.LocalModels;

public sealed record LocalModelApprovalStateAnchor(
    int SchemaVersion,
    string StateKind,
    string StorageId,
    string StateSha256,
    long StateBytes);

public interface ILocalModelApprovalStateAnchorStore
{
    Task<LocalModelApprovalStateAnchor?> ReadAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        LocalModelApprovalStateAnchor anchor,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default);
}

public sealed class WindowsLocalModelApprovalStateAnchorStore : ILocalModelApprovalStateAnchorStore
{
    public const int CurrentSchemaVersion = 1;
    private const int MaximumAnchorCharacters = 1024;
    private const int MaximumStateBytes = 64 * 1024;
    private const string TargetPrefix = "DCx/LocalModels/ApprovalState/v1/";

    private static readonly object VaultLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 4,
    };

    private readonly WindowsCredentialVault vault;

    public WindowsLocalModelApprovalStateAnchorStore()
        : this(new WindowsCredentialVault())
    {
    }

    internal WindowsLocalModelApprovalStateAnchorStore(WindowsCredentialVault vault) =>
        this.vault = vault ?? throw new ArgumentNullException(nameof(vault));

    public Task<LocalModelApprovalStateAnchor?> ReadAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(stateKind, storageId);
        lock (VaultLock)
        {
            return Task.FromResult(ReadCore(stateKind, storageId));
        }
    }

    public Task WriteAsync(
        LocalModelApprovalStateAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnchor(anchor);
        var normalized = anchor with { StateSha256 = anchor.StateSha256.ToLowerInvariant() };
        lock (VaultLock)
        {
            try
            {
                vault.Save(
                    TargetName(normalized.StateKind, normalized.StorageId),
                    JsonSerializer.Serialize(normalized, JsonOptions));
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    $"The protected local-model approval anchor could not be written: {exception.GetType().Name}.");
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string stateKind,
        string storageId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(stateKind, storageId);
        lock (VaultLock)
        {
            try
            {
                _ = vault.Delete(TargetName(stateKind, storageId));
            }
            catch (Win32Exception exception)
            {
                throw new InvalidOperationException(
                    $"The protected local-model approval anchor could not be cleared: {exception.GetType().Name}.");
            }
        }
        return Task.CompletedTask;
    }

    internal static string TargetName(string stateKind, string storageId)
    {
        ValidateIdentity(stateKind, storageId);
        var identityBytes = Encoding.UTF8.GetBytes(stateKind + "\n" + storageId);
        try
        {
            return TargetPrefix + Convert.ToHexStringLower(SHA256.HashData(identityBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identityBytes);
        }
    }

    private LocalModelApprovalStateAnchor? ReadCore(string stateKind, string storageId)
    {
        string? value;
        try
        {
            value = vault.Read(TargetName(stateKind, storageId));
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"The protected local-model approval anchor could not be read: {exception.GetType().Name}.");
        }
        if (value is null) return null;
        if (value.Length is <= 0 or > MaximumAnchorCharacters || value.Contains('\0'))
        {
            throw new InvalidOperationException("The protected local-model approval anchor is outside its limit.");
        }

        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            RejectDuplicateProperties(document.RootElement);
            var anchor = JsonSerializer.Deserialize<LocalModelApprovalStateAnchor>(value, JsonOptions)
                ?? throw new InvalidOperationException("The protected local-model approval anchor is empty.");
            ValidateAnchor(anchor);
            if (!string.Equals(anchor.StateKind, stateKind, StringComparison.Ordinal) ||
                !string.Equals(anchor.StorageId, storageId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The protected local-model approval anchor belongs to another state store.");
            }
            return anchor with { StateSha256 = anchor.StateSha256.ToLowerInvariant() };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The protected local-model approval anchor is malformed.",
                exception);
        }
    }

    private static void ValidateAnchor(LocalModelApprovalStateAnchor anchor)
    {
        if (anchor.SchemaVersion != CurrentSchemaVersion ||
            anchor.StateBytes is <= 0 or > MaximumStateBytes)
        {
            throw new InvalidOperationException("The protected local-model approval anchor is invalid.");
        }
        ValidateIdentity(anchor.StateKind, anchor.StorageId);
        ValidateSha256(anchor.StateSha256, "state");
    }

    internal static void ValidateIdentity(string stateKind, string storageId)
    {
        if (string.IsNullOrWhiteSpace(stateKind) || stateKind.Length > 64 ||
            stateKind != stateKind.Trim() ||
            stateKind.Any(character => !(character is >= 'a' and <= 'z' || char.IsAsciiDigit(character) || character == '-')))
        {
            throw new InvalidOperationException("The protected local-model approval state kind is invalid.");
        }
        ValidateSha256(storageId, "storage");
    }

    private static void ValidateSha256(string? value, string label)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                $"The protected local-model approval {label} digest is invalid.");
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
                    throw new InvalidOperationException(
                        "The protected local-model approval anchor has duplicate properties.");
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

internal static class LocalModelApprovalStateProtector
{
    public static string StorageId(string statePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        var canonical = Path.GetFullPath(statePath);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
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

    public static async Task<bool> MatchesAsync(
        string stateKind,
        string storageId,
        ReadOnlyMemory<byte> stateBytes,
        ILocalModelApprovalStateAnchorStore anchorStore,
        CancellationToken cancellationToken)
    {
        if (stateBytes.IsEmpty) return false;
        LocalModelApprovalStateAnchor? anchor;
        try
        {
            anchor = await anchorStore.ReadAsync(stateKind, storageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
        if (anchor is null || anchor.StateBytes != stateBytes.Length) return false;
        var digest = Convert.ToHexStringLower(SHA256.HashData(stateBytes.Span));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(digest),
            Encoding.ASCII.GetBytes(anchor.StateSha256));
    }

    public static Task ProtectAsync(
        string stateKind,
        string storageId,
        ReadOnlyMemory<byte> stateBytes,
        ILocalModelApprovalStateAnchorStore anchorStore,
        CancellationToken cancellationToken)
    {
        if (stateBytes.IsEmpty)
        {
            throw new InvalidOperationException("An empty local-model approval state cannot be protected.");
        }
        return anchorStore.WriteAsync(
            new LocalModelApprovalStateAnchor(
                WindowsLocalModelApprovalStateAnchorStore.CurrentSchemaVersion,
                stateKind,
                storageId,
                Convert.ToHexStringLower(SHA256.HashData(stateBytes.Span)),
                stateBytes.Length),
            cancellationToken);
    }
}
