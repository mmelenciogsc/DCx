namespace DCx.Core.Control;

public sealed class BrowserControlCoordinator
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private BrowserControlMode _mode = BrowserControlMode.User;
    private AgentControlRequest? _request;
    private AgentActionLease? _lease;

    public BrowserControlCoordinator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<BrowserControlChangedEventArgs>? ModeChanged;

    public BrowserControlMode Mode
    {
        get
        {
            lock (_sync)
            {
                return _mode;
            }
        }
    }

    public AgentControlRequest? CurrentRequest
    {
        get
        {
            lock (_sync)
            {
                return _request;
            }
        }
    }

    public AgentActionLease? CurrentLease
    {
        get
        {
            lock (_sync)
            {
                return _lease;
            }
        }
    }

    public AgentControlRequest RequestAgentControl(string purpose, Uri allowedTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        var origin = BrowserOrigin.FromUri(allowedTarget);
        BrowserControlChangedEventArgs transition;
        AgentControlRequest request;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.User, BrowserControlMode.Cancelled);
            request = new AgentControlRequest(Guid.NewGuid(), purpose.Trim(), origin, UtcNow());
            _request = request;
            _lease = null;
            transition = TransitionUnsafe(BrowserControlMode.AgentPending, "Agent control requested. User approval is required.");
        }

        Raise(transition);
        return request;
    }

    public AgentActionLease ApproveAgentControl(Guid requestId, Guid tabId, long navigationGeneration)
    {
        BrowserControlChangedEventArgs transition;
        AgentActionLease lease;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.AgentPending);
            if (_request?.RequestId != requestId)
            {
                throw new InvalidOperationException("The approval does not match the pending request.");
            }

            lease = IssueLeaseUnsafe(tabId, navigationGeneration);
            transition = TransitionUnsafe(BrowserControlMode.Agent, "User approved the scoped agent-control request.");
        }

        Raise(transition);
        return lease;
    }

    public bool IsDomActionAllowed(
        Guid leaseId,
        Guid tabId,
        Uri currentAddress,
        long navigationGeneration,
        out string rejectionReason)
    {
        lock (_sync)
        {
            if (!TryGetHttpOrigin(currentAddress, out var currentOrigin))
            {
                rejectionReason = "The current page address is not an absolute HTTP or HTTPS URI.";
                return false;
            }

            if (!ValidateActiveLeaseUnsafe(leaseId, tabId, out rejectionReason))
            {
                return false;
            }

            if (_lease!.NavigationGeneration != navigationGeneration)
            {
                rejectionReason = "The page changed after this agent lease was issued.";
                return false;
            }

            if (!string.Equals(_lease.AllowedOrigin, currentOrigin, StringComparison.OrdinalIgnoreCase))
            {
                rejectionReason = "The current page origin is outside the approved agent scope.";
                return false;
            }

            rejectionReason = string.Empty;
            return true;
        }
    }

    public bool IsNavigationAllowed(Guid leaseId, Guid tabId, Uri target, out string rejectionReason)
    {
        lock (_sync)
        {
            if (!TryGetHttpOrigin(target, out var targetOrigin))
            {
                rejectionReason = "The navigation target is not an absolute HTTP or HTTPS URI.";
                return false;
            }

            if (!ValidateActiveLeaseUnsafe(leaseId, tabId, out rejectionReason))
            {
                return false;
            }

            if (!string.Equals(_lease!.AllowedOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase))
            {
                rejectionReason = "The target origin is outside the approved agent scope.";
                return false;
            }

            rejectionReason = string.Empty;
            return true;
        }
    }

    public void PauseForApprovedNavigation(Guid leaseId, Guid tabId, Uri target)
    {
        BrowserControlChangedEventArgs transition;
        lock (_sync)
        {
            if (!IsNavigationAllowedUnsafe(leaseId, tabId, target, out var rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            _lease = null;
            transition = TransitionUnsafe(BrowserControlMode.PausedForNavigation, "Agent lease paused while the approved page navigates.");
        }

        Raise(transition);
    }

    public AgentActionLease ResumeAfterApprovedNavigation(Guid tabId, Uri currentAddress, long navigationGeneration)
    {
        BrowserControlChangedEventArgs transition;
        AgentActionLease lease;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.PausedForNavigation);
            if (_request is null ||
                !string.Equals(_request.AllowedOrigin, BrowserOrigin.FromUri(currentAddress), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The completed navigation left the approved origin.");
            }

            lease = IssueLeaseUnsafe(tabId, navigationGeneration);
            transition = TransitionUnsafe(BrowserControlMode.Agent, "Approved navigation completed and the scoped lease was renewed.");
        }

        Raise(transition);
        return lease;
    }

    public void YieldToUser(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        BrowserControlChangedEventArgs transition;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.Agent, BrowserControlMode.PausedForNavigation);
            _lease = null;
            transition = TransitionUnsafe(BrowserControlMode.UserTakeover, reason.Trim());
        }

        Raise(transition);
    }

    public AgentActionLease HandBackToAgent(Guid tabId, Uri currentAddress, long navigationGeneration)
    {
        BrowserControlChangedEventArgs transition;
        AgentActionLease lease;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.UserTakeover);
            if (_request is null ||
                !string.Equals(_request.AllowedOrigin, BrowserOrigin.FromUri(currentAddress), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The current page is outside the previously approved agent scope.");
            }

            lease = IssueLeaseUnsafe(tabId, navigationGeneration);
            transition = TransitionUnsafe(BrowserControlMode.Agent, "User explicitly handed control back to the agent.");
        }

        Raise(transition);
        return lease;
    }

    public bool NotifyPhysicalUserInput(string reason = "Physical user input revoked the agent lease.")
    {
        BrowserControlChangedEventArgs? transition = null;
        lock (_sync)
        {
            if (_mode is BrowserControlMode.Agent or BrowserControlMode.PausedForNavigation)
            {
                _lease = null;
                transition = TransitionUnsafe(BrowserControlMode.UserTakeover, reason);
            }
        }

        if (transition is null)
        {
            return false;
        }

        Raise(transition);
        return true;
    }

    public void CompleteAgentWork(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        BrowserControlChangedEventArgs transition;
        lock (_sync)
        {
            EnsureMode(BrowserControlMode.Agent);
            _lease = null;
            _request = null;
            transition = TransitionUnsafe(BrowserControlMode.User, reason.Trim());
        }

        Raise(transition);
    }

    public void RejectOrCancel(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        BrowserControlChangedEventArgs transition;
        lock (_sync)
        {
            _lease = null;
            _request = null;
            transition = TransitionUnsafe(BrowserControlMode.Cancelled, reason.Trim());
        }

        Raise(transition);
    }

    public void ResetToUser(string reason = "User control restored.")
    {
        BrowserControlChangedEventArgs transition;
        lock (_sync)
        {
            _lease = null;
            _request = null;
            transition = TransitionUnsafe(BrowserControlMode.User, reason);
        }

        Raise(transition);
    }

    private bool IsNavigationAllowedUnsafe(Guid leaseId, Guid tabId, Uri target, out string rejectionReason)
    {
        if (!TryGetHttpOrigin(target, out var targetOrigin))
        {
            rejectionReason = "The navigation target is not an absolute HTTP or HTTPS URI.";
            return false;
        }

        if (!ValidateActiveLeaseUnsafe(leaseId, tabId, out rejectionReason))
        {
            return false;
        }

        if (!string.Equals(_lease!.AllowedOrigin, targetOrigin, StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = "The target origin is outside the approved agent scope.";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private static bool TryGetHttpOrigin(Uri? uri, out string origin)
    {
        origin = string.Empty;
        if (uri is null)
        {
            return false;
        }

        try
        {
            origin = BrowserOrigin.FromUri(uri);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool ValidateActiveLeaseUnsafe(Guid leaseId, Guid tabId, out string rejectionReason)
    {
        if (_mode != BrowserControlMode.Agent || _lease is null)
        {
            rejectionReason = "The agent does not hold control.";
            return false;
        }

        if (_lease.LeaseId != leaseId || _lease.TabId != tabId)
        {
            rejectionReason = "The action lease does not match the approved browser tab.";
            return false;
        }

        if (_lease.ExpiresAtUtc <= UtcNow())
        {
            _lease = null;
            rejectionReason = "The agent action lease expired.";
            return false;
        }

        rejectionReason = string.Empty;
        return true;
    }

    private AgentActionLease IssueLeaseUnsafe(Guid tabId, long navigationGeneration)
    {
        if (_request is null)
        {
            throw new InvalidOperationException("There is no approved request to lease.");
        }

        var now = UtcNow();
        _lease = new AgentActionLease(
            Guid.NewGuid(),
            tabId,
            _request.AllowedOrigin,
            navigationGeneration,
            now,
            now.Add(LeaseLifetime));
        return _lease;
    }

    private BrowserControlChangedEventArgs TransitionUnsafe(BrowserControlMode newMode, string reason)
    {
        var previous = _mode;
        _mode = newMode;
        return new BrowserControlChangedEventArgs(previous, newMode, reason);
    }

    private void EnsureMode(params BrowserControlMode[] allowed)
    {
        if (!allowed.Contains(_mode))
        {
            throw new InvalidOperationException($"The operation is not valid while control mode is {_mode}.");
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow();

    private void Raise(BrowserControlChangedEventArgs args) => ModeChanged?.Invoke(this, args);
}
