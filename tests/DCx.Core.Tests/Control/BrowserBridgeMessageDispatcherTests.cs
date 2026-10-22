using System.Text;
using System.Text.Json;
using DCx.Core.Control;

namespace DCx.Core.Tests.Control;

public sealed class BrowserBridgeMessageDispatcherTests
{
    private static readonly Uri ApprovedPage = new("https://fixture.example/login");
    private static readonly Uri ApprovedSource = new("https://fixture.example/bridge");
    private static readonly Guid BridgeTab = Guid.Parse("51515151-5151-5151-5151-515151515151");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private const string SessionId = "session-opaque-4eb8f7c2";

    [Fact]
    public void PageTakeControlRevokesAgentLeaseAndOnlyNativeHandbackRestoresIt()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        var originalLease = coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        var takeover = dispatcher.Dispatch(ValidatedMessage("user-input", "{\"event\":\"take-control-requested\"}"));

        Assert.True(takeover.ControlRevoked);
        Assert.Equal("Agent control was yielded to the user.", takeover.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
        Assert.False(coordinator.IsDomActionAllowed(originalLease.LeaseId, tab, ApprovedPage, 7, out _));

        var pageHandback = dispatcher.Dispatch(ValidatedMessage("user-input", "{\"event\":\"hand-control-back\"}"));

        Assert.False(pageHandback.ControlRevoked);
        Assert.Equal("Page hand-control-back notifications cannot grant agent control.", pageHandback.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);

        var renewedLease = coordinator.HandBackToAgent(tab, ApprovedPage, 7);

        Assert.Equal(BrowserControlMode.Agent, coordinator.Mode);
        Assert.NotEqual(originalLease.LeaseId, renewedLease.LeaseId);
        Assert.True(coordinator.IsDomActionAllowed(renewedLease.LeaseId, tab, ApprovedPage, 7, out _));
    }

    [Theory]
    [InlineData("fixture-ready", "{\"ready\":true}")]
    [InlineData("proof-status", "{\"state\":\"ready\"}")]
    public void AcceptedInformationalMessagesCarryNoControlAuthority(string type, string payload)
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        var lease = coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        var outcome = dispatcher.Dispatch(ValidatedMessage(type, payload));

        Assert.False(outcome.ControlRevoked);
        Assert.Equal("The bridge message is informational and cannot change control.", outcome.Reason);
        Assert.Equal(BrowserControlMode.Agent, coordinator.Mode);
        Assert.Equal(lease, coordinator.CurrentLease);
    }

    [Theory]
    [InlineData("unknown", "{}")]
    [InlineData("user-input", "{}")]
    [InlineData("user-input", "{\"event\":\"unknown\"}")]
    public void InertUnknownAndMalformedMessagesCannotGrantAuthority(string type, string payload)
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        coordinator.YieldToUser("Native user takeover.");
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        var outcome = dispatcher.Dispatch(Message(type, payload));

        Assert.False(outcome.ControlRevoked);
        Assert.NotEmpty(outcome.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.NotNull(coordinator.CurrentRequest);
        Assert.Null(coordinator.CurrentLease);
    }

    [Fact]
    public void NullMessageCannotGrantAuthority()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        var outcome = dispatcher.Dispatch(null);

        Assert.True(outcome.ControlRevoked);
        Assert.Equal("No bridge message was available.", outcome.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.NotNull(coordinator.CurrentRequest);
        Assert.Null(coordinator.CurrentLease);
    }

    [Fact]
    public void MalformedOrDisposedPayloadRevokesTheActiveLeaseWithoutThrowing()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        JsonElement disposedPayload;
        using (var document = JsonDocument.Parse("{\"event\":\"take-control-requested\"}"))
        {
            disposedPayload = document.RootElement;
        }

        var outcome = dispatcher.Dispatch(new BrowserBridgeMessage("user-input", disposedPayload));

        Assert.True(outcome.ControlRevoked);
        Assert.Equal("The bridge message payload is not available.", outcome.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
    }

    [Fact]
    public void DisposedInformationalPayloadAlsoRevokesTheActiveLeaseWithoutThrowing()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        JsonElement disposedPayload;
        using (var document = JsonDocument.Parse("{\"ready\":true}"))
        {
            disposedPayload = document.RootElement;
        }

        var outcome = dispatcher.Dispatch(new BrowserBridgeMessage("fixture-ready", disposedPayload));

        Assert.True(outcome.ControlRevoked);
        Assert.Equal("The bridge message payload is not available.", outcome.Reason);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
    }

    [Fact]
    public void UnknownMessageTypeRevokesTheActiveLeaseWithoutGrantingAuthority()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Fixture proof", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, tab, 7);
        var dispatcher = new BrowserBridgeMessageDispatcher(coordinator);

        var outcome = dispatcher.Dispatch(Message("host-action", "{}"));

        Assert.True(outcome.ControlRevoked);
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
    }

    private static BrowserBridgeMessage ValidatedMessage(string type, string payload)
    {
        var validator = new BrowserBridgeMessageValidator(
            ApprovedPage,
            SessionId,
            BridgeTab,
            7,
            Now.AddMinutes(5),
            new FixedTimeProvider(Now));
        var accepted = validator.TryValidate(
            ApprovedSource,
            Encoding.UTF8.GetBytes(CreateEnvelope(type, payload)),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal(string.Empty, rejectionCode);
        return message;
    }

    private static BrowserBridgeMessage Message(string type, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return new BrowserBridgeMessage(type, document.RootElement.Clone());
    }

    private static string CreateEnvelope(string type, string payload) =>
        $"{{\"version\":1,\"sessionId\":\"{SessionId}\",\"type\":{JsonSerializer.Serialize(type)},\"tabId\":\"{BridgeTab:D}\",\"navigationGeneration\":7,\"payload\":{payload}}}";

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
