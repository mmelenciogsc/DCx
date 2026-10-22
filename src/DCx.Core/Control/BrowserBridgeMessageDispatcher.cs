using System.Text.Json;

namespace DCx.Core.Control;

/// <summary>
/// Interprets already-validated, inert page bridge messages without granting page authority.
/// </summary>
public sealed class BrowserBridgeMessageDispatcher
{
    private const string UserInputType = "user-input";
    private const string TakeControlRequestedEvent = "take-control-requested";
    private const string HandControlBackEvent = "hand-control-back";

    private readonly BrowserControlCoordinator _coordinator;

    public BrowserBridgeMessageDispatcher(BrowserControlCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    /// <summary>
    /// Handles an accepted bridge message. Only a page request to take control may yield an
    /// existing agent lease; pages cannot create, approve, renew, or restore a lease.
    /// </summary>
    public (bool ControlRevoked, string Reason) Dispatch(BrowserBridgeMessage? message)
    {
        if (message is null)
        {
            return RejectAndRevoke("No bridge message was available.");
        }

        if (!IsReadablePayload(message.Payload))
        {
            return RejectAndRevoke("The bridge message payload is not available.");
        }

        if (!string.Equals(message.Type, UserInputType, StringComparison.Ordinal))
        {
            return message.Type is "fixture-ready" or "proof-status"
                ? (false, "The bridge message is informational and cannot change control.")
                : RejectAndRevoke("The bridge message type is not accepted.");
        }

        if (!TryGetExactUserInputEvent(message.Payload, out var inputEvent))
        {
            return RejectAndRevoke("The bridge message is not an accepted control notification.");
        }

        if (string.Equals(inputEvent, HandControlBackEvent, StringComparison.Ordinal))
        {
            return (false, "Page hand-control-back notifications cannot grant agent control.");
        }

        if (!string.Equals(inputEvent, TakeControlRequestedEvent, StringComparison.Ordinal))
        {
            return RejectAndRevoke("The bridge message is not an accepted control notification.");
        }

        try
        {
            _coordinator.YieldToUser("Page reported a request to take control.");
            return (true, "Agent control was yielded to the user.");
        }
        catch (InvalidOperationException)
        {
            return (false, "No active agent control was available to yield.");
        }
    }

    private static bool TryGetExactUserInputEvent(JsonElement payload, out string inputEvent)
    {
        inputEvent = string.Empty;
        try
        {
            if (payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var count = 0;
            foreach (var property in payload.EnumerateObject())
            {
                if (property.Name != "event" || property.Value.ValueKind != JsonValueKind.String || ++count > 1)
                {
                    return false;
                }

                inputEvent = property.Value.GetString() ?? string.Empty;
            }

            return count == 1;
        }
        catch (Exception)
        {
            inputEvent = string.Empty;
            return false;
        }
    }

    private (bool ControlRevoked, string Reason) RejectAndRevoke(string reason)
    {
        var revoked = _coordinator.NotifyPhysicalUserInput("An invalid page bridge message revoked the agent lease.");
        return (revoked, reason);
    }

    private static bool IsReadablePayload(JsonElement payload)
    {
        try
        {
            return payload.ValueKind != JsonValueKind.Undefined &&
                   payload.Clone().ValueKind != JsonValueKind.Undefined;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
