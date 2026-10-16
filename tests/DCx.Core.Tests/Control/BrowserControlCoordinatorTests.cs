using DCx.Core.Control;

namespace DCx.Core.Tests.Control;

public sealed class BrowserControlCoordinatorTests
{
    private static readonly Uri ApprovedPage = new("https://fixture.example/login");

    [Fact]
    public void AgentActionsRequireExplicitApprovalAndMatchingTabOriginGeneration()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var otherTab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Test handoff", ApprovedPage);

        Assert.Equal(BrowserControlMode.AgentPending, coordinator.Mode);
        Assert.False(coordinator.IsDomActionAllowed(Guid.NewGuid(), tab, ApprovedPage, 4, out _));

        var lease = coordinator.ApproveAgentControl(request.RequestId, tab, 4);

        Assert.True(coordinator.IsDomActionAllowed(lease.LeaseId, tab, ApprovedPage, 4, out _));
        Assert.False(coordinator.IsDomActionAllowed(lease.LeaseId, otherTab, ApprovedPage, 4, out _));
        Assert.False(coordinator.IsDomActionAllowed(lease.LeaseId, tab, new Uri("https://other.example/"), 4, out _));
        Assert.False(coordinator.IsDomActionAllowed(lease.LeaseId, tab, ApprovedPage, 5, out _));
    }

    [Fact]
    public void ApprovedSameOriginNavigationPausesAndRenewsLease()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Navigate", ApprovedPage);
        var firstLease = coordinator.ApproveAgentControl(request.RequestId, tab, 1);

        Assert.True(coordinator.IsNavigationAllowed(
            firstLease.LeaseId,
            tab,
            new Uri("https://fixture.example/next"),
            out _));

        coordinator.PauseForApprovedNavigation(
            firstLease.LeaseId,
            tab,
            new Uri("https://fixture.example/next"));
        Assert.Equal(BrowserControlMode.PausedForNavigation, coordinator.Mode);

        var renewed = coordinator.ResumeAfterApprovedNavigation(
            tab,
            new Uri("https://fixture.example/next"),
            2);
        Assert.Equal(BrowserControlMode.Agent, coordinator.Mode);
        Assert.NotEqual(firstLease.LeaseId, renewed.LeaseId);
        Assert.True(coordinator.IsDomActionAllowed(
            renewed.LeaseId,
            tab,
            new Uri("https://fixture.example/next"),
            2,
            out _));
    }

    [Fact]
    public void CrossOriginNavigationIsDenied()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Navigate", ApprovedPage);
        var lease = coordinator.ApproveAgentControl(request.RequestId, tab, 1);

        Assert.False(coordinator.IsNavigationAllowed(
            lease.LeaseId,
            tab,
            new Uri("https://attacker.example/"),
            out var reason));
        Assert.Contains("outside", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PhysicalUserInputRevokesLeaseAndRequiresExplicitHandback()
    {
        var coordinator = new BrowserControlCoordinator();
        var tab = Guid.NewGuid();
        var request = coordinator.RequestAgentControl("Handoff", ApprovedPage);
        var lease = coordinator.ApproveAgentControl(request.RequestId, tab, 1);

        Assert.True(coordinator.NotifyPhysicalUserInput());
        Assert.Equal(BrowserControlMode.UserTakeover, coordinator.Mode);
        Assert.False(coordinator.IsDomActionAllowed(lease.LeaseId, tab, ApprovedPage, 1, out _));

        var renewed = coordinator.HandBackToAgent(tab, ApprovedPage, 1);
        Assert.Equal(BrowserControlMode.Agent, coordinator.Mode);
        Assert.True(coordinator.IsDomActionAllowed(renewed.LeaseId, tab, ApprovedPage, 1, out _));
    }

    [Fact]
    public void RejectedRequestNeverCreatesLease()
    {
        var coordinator = new BrowserControlCoordinator();
        coordinator.RequestAgentControl("Rejected", ApprovedPage);

        coordinator.RejectOrCancel("User rejected the request.");

        Assert.Equal(BrowserControlMode.Cancelled, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
        Assert.Null(coordinator.CurrentRequest);
    }

    [Fact]
    public void CompletionReturnsControlToUser()
    {
        var coordinator = new BrowserControlCoordinator();
        var request = coordinator.RequestAgentControl("Complete", ApprovedPage);
        coordinator.ApproveAgentControl(request.RequestId, Guid.NewGuid(), 1);

        coordinator.CompleteAgentWork("Validated task completed.");

        Assert.Equal(BrowserControlMode.User, coordinator.Mode);
        Assert.Null(coordinator.CurrentLease);
    }
}

