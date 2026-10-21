using System.Text;
using System.Text.Json;

namespace DCx.Core.Control;

public sealed record BrowserBridgeMessage(string Type, JsonElement Payload);

/// <summary>
/// Validates the inert, page-to-host messages emitted by an approved browser fixture.
/// </summary>
public sealed class BrowserBridgeMessageValidator
{
    private const int ProtocolVersion = 1;
    private const int MaximumMessageUtf8Bytes = 4 * 1024;
    private const int MaximumSessionIdUtf8Bytes = 128;
    private const int MaximumTypeUtf8Bytes = 64;
    private const int MaximumTabIdUtf8Bytes = 64;
    private const int MaximumPayloadUtf8Bytes = 1024;
    private const int MaximumPayloadStringUtf8Bytes = 1024;
    private const int MaximumPayloadPropertyNameUtf8Bytes = 128;
    private const int MaximumPayloadMembers = 16;
    private const int MaximumPayloadDepth = 4;

    private const string InvalidEnvelope = "invalid-envelope";
    private const string UntrustedSource = "untrusted-source";
    private const string BindingMismatch = "binding-mismatch";
    private const string UnsupportedType = "unsupported-type";
    private const string InactiveSession = "inactive-session";

    private const string FixtureReadyType = "fixture-ready";
    private const string UserInputType = "user-input";
    private const string ProofStatusType = "proof-status";
    private const string TakeControlRequestedEvent = "take-control-requested";
    private const string HandControlBackEvent = "hand-control-back";
    private const string ReadyState = "ready";
    private const string ManualStepCompleteState = "manual-step-complete";
    private const string CompleteState = "complete";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaximumPayloadDepth + 4,
    };

    private readonly string _approvedOrigin;
    private readonly string _sessionId;
    private readonly Guid _tabId;
    private readonly long _navigationGeneration;
    private readonly DateTimeOffset _expiresAtUtc;
    private readonly TimeProvider _timeProvider;

    public BrowserBridgeMessageValidator(
        Uri approvedUri,
        string sessionId,
        Guid tabId,
        long navigationGeneration,
        DateTimeOffset expiresAtUtc,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(approvedUri);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!IsSafeString(sessionId, MaximumSessionIdUtf8Bytes))
        {
            throw new ArgumentException("The browser bridge session identifier is invalid.", nameof(sessionId));
        }

        if (tabId == Guid.Empty)
        {
            throw new ArgumentException("The browser bridge tab identifier is required.", nameof(tabId));
        }

        if (navigationGeneration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(navigationGeneration));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= timeProvider.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        _approvedOrigin = BrowserOrigin.FromUri(approvedUri);
        _sessionId = sessionId;
        _tabId = tabId;
        _navigationGeneration = navigationGeneration;
        _expiresAtUtc = expiresAtUtc;
        _timeProvider = timeProvider;
    }

    public bool TryValidate(
        Uri? source,
        ReadOnlyMemory<byte> messageUtf8,
        out BrowserBridgeMessage? message,
        out string rejectionCode)
    {
        message = null;
        if (_timeProvider.GetUtcNow() >= _expiresAtUtc)
        {
            rejectionCode = InactiveSession;
            return false;
        }

        if (!HasApprovedOrigin(source))
        {
            rejectionCode = UntrustedSource;
            return false;
        }

        if (messageUtf8.Length is <= 0 or > MaximumMessageUtf8Bytes)
        {
            rejectionCode = InvalidEnvelope;
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(messageUtf8, DocumentOptions);
            if (!TryReadEnvelope(
                    document.RootElement,
                    out var sessionId,
                    out var type,
                    out var tabId,
                    out var navigationGeneration,
                    out var payload))
            {
                rejectionCode = InvalidEnvelope;
                return false;
            }

            if (!string.Equals(sessionId, _sessionId, StringComparison.Ordinal) ||
                tabId != _tabId ||
                navigationGeneration != _navigationGeneration)
            {
                rejectionCode = BindingMismatch;
                return false;
            }

            if (!IsSupportedType(type))
            {
                rejectionCode = UnsupportedType;
                return false;
            }

            if (!HasSupportedPayload(type, payload))
            {
                rejectionCode = InvalidEnvelope;
                return false;
            }

            message = new BrowserBridgeMessage(type, payload.Clone());
            rejectionCode = string.Empty;
            return true;
        }
        catch (JsonException)
        {
            rejectionCode = InvalidEnvelope;
            return false;
        }
    }

    private bool HasApprovedOrigin(Uri? source)
    {
        if (source is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                _approvedOrigin,
                BrowserOrigin.FromUri(source),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadEnvelope(
        JsonElement root,
        out string sessionId,
        out string type,
        out Guid tabId,
        out long navigationGeneration,
        out JsonElement payload)
    {
        sessionId = string.Empty;
        type = string.Empty;
        tabId = Guid.Empty;
        navigationGeneration = 0;
        payload = default;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var hasVersion = false;
        var hasSessionId = false;
        var hasType = false;
        var hasTabId = false;
        var hasNavigationGeneration = false;
        var hasPayload = false;

        foreach (var property in root.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "version":
                    if (hasVersion || property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt32(out var version) || version != ProtocolVersion)
                    {
                        return false;
                    }

                    hasVersion = true;
                    break;

                case "sessionId":
                    if (hasSessionId || !TryReadBoundedString(property.Value, MaximumSessionIdUtf8Bytes, out sessionId))
                    {
                        return false;
                    }

                    hasSessionId = true;
                    break;

                case "type":
                    if (hasType || !TryReadBoundedString(property.Value, MaximumTypeUtf8Bytes, out type))
                    {
                        return false;
                    }

                    hasType = true;
                    break;

                case "tabId":
                    if (hasTabId || !TryReadBoundedString(property.Value, MaximumTabIdUtf8Bytes, out var tabIdText) ||
                        !Guid.TryParseExact(tabIdText, "D", out tabId))
                    {
                        return false;
                    }

                    hasTabId = true;
                    break;

                case "navigationGeneration":
                    if (hasNavigationGeneration || property.Value.ValueKind != JsonValueKind.Number ||
                        !property.Value.TryGetInt64(out navigationGeneration) || navigationGeneration <= 0)
                    {
                        return false;
                    }

                    hasNavigationGeneration = true;
                    break;

                case "payload":
                    if (hasPayload || !IsBoundedPayload(property.Value, depth: 0))
                    {
                        return false;
                    }

                    payload = property.Value;
                    hasPayload = true;
                    break;

                default:
                    return false;
            }
        }

        return hasVersion && hasSessionId && hasType && hasTabId && hasNavigationGeneration && hasPayload;
    }

    private static bool IsSupportedType(string type) =>
        type is FixtureReadyType or UserInputType or ProofStatusType;

    private static bool HasSupportedPayload(string type, JsonElement payload)
    {
        return type switch
        {
            FixtureReadyType => HasExactProperty(payload, "ready", JsonValueKind.True, out _),
            UserInputType => HasExactEnumProperty(
                payload,
                "event",
                TakeControlRequestedEvent,
                HandControlBackEvent),
            ProofStatusType => HasExactEnumProperty(
                payload,
                "state",
                ReadyState,
                ManualStepCompleteState,
                CompleteState),
            _ => false,
        };
    }

    private static bool HasExactProperty(
        JsonElement payload,
        string propertyName,
        JsonValueKind requiredKind,
        out JsonElement value)
    {
        value = default;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var count = 0;
        foreach (var property in payload.EnumerateObject())
        {
            if (property.Name != propertyName || ++count > 1)
            {
                return false;
            }

            value = property.Value;
        }

        return count == 1 && value.ValueKind == requiredKind;
    }

    private static bool HasExactEnumProperty(
        JsonElement payload,
        string propertyName,
        params string[] allowedValues)
    {
        if (!HasExactProperty(payload, propertyName, JsonValueKind.String, out var value))
        {
            return false;
        }

        var candidate = value.GetString();
        foreach (var allowedValue in allowedValues)
        {
            if (string.Equals(candidate, allowedValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadBoundedString(JsonElement element, int maximumUtf8Bytes, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = element.GetString();
        if (!IsSafeString(candidate, maximumUtf8Bytes))
        {
            return false;
        }

        value = candidate!;
        return true;
    }

    private static bool IsBoundedPayload(JsonElement element, int depth)
    {
        if (depth > MaximumPayloadDepth ||
            Encoding.UTF8.GetByteCount(element.GetRawText()) > MaximumPayloadUtf8Bytes)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var propertyNames = new HashSet<string>(StringComparer.Ordinal);
                var count = 0;
                foreach (var property in element.EnumerateObject())
                {
                    if (++count > MaximumPayloadMembers ||
                        !IsSafeString(property.Name, MaximumPayloadPropertyNameUtf8Bytes) ||
                        !propertyNames.Add(property.Name) ||
                        !IsBoundedPayload(property.Value, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            case JsonValueKind.Array:
            {
                var count = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (++count > MaximumPayloadMembers || !IsBoundedPayload(item, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            }

            case JsonValueKind.String:
                return IsSafeString(element.GetString(), MaximumPayloadStringUtf8Bytes);

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;

            default:
                return false;
        }
    }

    private static bool IsSafeString(string? value, int maximumUtf8Bytes)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}
