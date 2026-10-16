using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DCx.Core.Broker;
using DCx.Desktop.Broker;

namespace DCx.Desktop;

public partial class NativeAppControlWindow : Window
{
    private readonly string _repositoryRoot;
    private readonly WindowsProcessBroker _processBroker;
    private readonly RestrictedUiaAdapter _uiaAdapter;
    private readonly ObservableCollection<string> _logItems = [];
    private ExternalControlMode _mode = ExternalControlMode.User;
    private int? _fixtureProcessId;
    private int _operationVersion;

    public NativeAppControlWindow(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _processBroker = new WindowsProcessBroker(_repositoryRoot);
        _uiaAdapter = new RestrictedUiaAdapter(_processBroker);
        InitializeComponent();
        ExternalActionLog.ItemsSource = _logItems;
    }

    private async void OnLaunchFixtureClick(object sender, RoutedEventArgs e)
    {
        if (_fixtureProcessId is not null)
        {
            SetInstruction("The controlled app is already running.");
            return;
        }

        var result = await _processBroker.ExecuteAsync(new BrokerRequest(BrokerOperation.LaunchControlledFixture));
        if (!result.Succeeded || result.ProcessId is null)
        {
            SetInstruction($"Launch denied or failed. {result.Message}");
            AddLog(result.Message);
            return;
        }

        _fixtureProcessId = result.ProcessId;
        LaunchFixtureButton.IsEnabled = false;
        CloseFixtureButton.IsEnabled = true;
        UiaApprovalPanel.Visibility = Visibility.Visible;
        ApproveUiaButton.Focus();
        SetMode(ExternalControlMode.ApprovalPending, "Controlled app launched. UI Automation requires separate approval.");
        SetInstruction("Review the UI Automation request. Reject leaves the external app entirely under your control.");
        AddLog($"Launched allowlisted fixture as process {result.ProcessId}; no elevation requested.");
        await _uiaAdapter.BringToForegroundAsync(result.ProcessId.Value);
    }

    private async void OnApproveUiaClick(object sender, RoutedEventArgs e)
    {
        if (_fixtureProcessId is not int processId)
        {
            SetInstruction("The controlled app is no longer available.");
            return;
        }

        var operation = ++_operationVersion;
        UiaApprovalPanel.Visibility = Visibility.Collapsed;
        SetMode(ExternalControlMode.Agent, "User approved UI Automation for the broker-tracked fixture.");
        AddLog("Scoped UI Automation approved for the tracked process only.");
        try
        {
            await _uiaAdapter.SetTextAsync(processId, "FixtureInput", "Agent prepared fixture text");
            if (operation != _operationVersion) return;
            await _uiaAdapter.InvokeAsync(processId, "FixtureApplyButton");
            if (operation != _operationVersion) return;
            await _uiaAdapter.FocusAsync(processId, "FixtureInput");
            if (operation != _operationVersion) return;

            SetMode(ExternalControlMode.UserTakeover, "Agent paused. User control restored in the external app.");
            SetInstruction("Your control in the external app. Replace Shared text with any non-secret fixture value. Return to DCx and activate Resume agent when ready.");
            ResumeExternalAgentButton.IsEnabled = true;
            AddLog("Agent applied fixed fixture text, focused Shared text, and yielded to the user.");
            await _uiaAdapter.BringToForegroundAsync(processId);
        }
        catch (Exception exception)
        {
            DenyAndReturnToUser($"UI Automation stopped safely. {exception.Message}");
        }
    }

    private void OnRejectUiaClick(object sender, RoutedEventArgs e)
    {
        UiaApprovalPanel.Visibility = Visibility.Collapsed;
        SetMode(ExternalControlMode.User, "User rejected UI Automation. External app remains user-controlled.");
        SetInstruction("No UI Automation action was performed.");
        AddLog("User rejected the scoped UI Automation request.");
    }

    private void OnTakeExternalControlClick(object sender, RoutedEventArgs e)
    {
        _operationVersion++;
        SetMode(ExternalControlMode.UserTakeover, "User explicitly took control; remaining agent steps cancelled.");
        SetInstruction("Your control. Resume only when you explicitly want the agent to inspect and apply the current fixture text.");
        ResumeExternalAgentButton.IsEnabled = _fixtureProcessId is not null;
        AddLog("User takeover invalidated the active UI Automation operation.");
    }

    private async void OnResumeExternalAgentClick(object sender, RoutedEventArgs e)
    {
        if (_fixtureProcessId is not int processId)
        {
            SetInstruction("The controlled app is no longer available.");
            return;
        }

        var operation = ++_operationVersion;
        SetMode(ExternalControlMode.Agent, "User explicitly handed the tracked app back to the agent.");
        ResumeExternalAgentButton.IsEnabled = false;
        try
        {
            var value = await _uiaAdapter.ReadTextAsync(processId, "FixtureInput");
            if (operation != _operationVersion) return;
            await _uiaAdapter.InvokeAsync(processId, "FixtureApplyButton");
            if (operation != _operationVersion) return;
            await _uiaAdapter.FocusAsync(processId, "FixtureInput");
            if (operation != _operationVersion) return;

            SetMode(ExternalControlMode.User, "Agent resumed, validated the fixture, and returned control and focus.");
            SetInstruction("Complete. You control the external app. Shared text retains your value and has keyboard focus.");
            AddLog($"Read and applied the user-approved fixture value ({value.Length} characters); value omitted from log.");
            await _uiaAdapter.BringToForegroundAsync(processId);
        }
        catch (Exception exception)
        {
            DenyAndReturnToUser($"Agent resume stopped safely. {exception.Message}");
        }
    }

    private async void OnInspectReadmeClick(object sender, RoutedEventArgs e)
    {
        InspectReadmeButton.IsEnabled = false;
        try
        {
            var result = await _processBroker.ExecuteAsync(new BrokerRequest(
                BrokerOperation.InspectPathReadOnly,
                Path.Combine(_repositoryRoot, "README.md")));
            SetInstruction(result.Succeeded
                ? "Read-only PowerShell inspection completed. File content was not displayed or logged."
                : $"Read-only inspection failed. {result.Message}");
            AddLog($"Restricted PowerShell inspection exit code: {result.ExitCode?.ToString() ?? "not started"}.");
        }
        finally
        {
            InspectReadmeButton.IsEnabled = true;
        }
    }

    private void OnCloseFixtureClick(object sender, RoutedEventArgs e)
    {
        if (_fixtureProcessId is not int processId)
        {
            return;
        }

        _operationVersion++;
        var requested = _processBroker.CloseTrackedWindow(processId);
        AddLog(requested ? "User requested the controlled app to close." : "Controlled app was already closed.");
        _fixtureProcessId = null;
        CloseFixtureButton.IsEnabled = false;
        LaunchFixtureButton.IsEnabled = true;
        ResumeExternalAgentButton.IsEnabled = false;
        UiaApprovalPanel.Visibility = Visibility.Collapsed;
        SetMode(ExternalControlMode.User, "Controlled app closed by explicit user action.");
        SetInstruction("Launch the controlled app to run another test.");
    }

    private void DenyAndReturnToUser(string message)
    {
        SetMode(ExternalControlMode.UserTakeover, message);
        SetInstruction("Your control. No further UI Automation action will run without Resume agent.");
        ResumeExternalAgentButton.IsEnabled = _fixtureProcessId is not null;
        AddLog(message);
    }

    private void SetMode(ExternalControlMode mode, string message)
    {
        _mode = mode;
        ExternalControlModeText.Text = $"Control: {FormatMode(mode)}. {message}";
        TakeExternalControlButton.IsEnabled = mode == ExternalControlMode.Agent;
    }

    private void SetInstruction(string message) => ExternalInstructionText.Text = message;

    private void AddLog(string message)
    {
        _logItems.Insert(0, $"{DateTimeOffset.Now:T} — {message}");
        while (_logItems.Count > 100)
        {
            _logItems.RemoveAt(_logItems.Count - 1);
        }
    }

    private static string FormatMode(ExternalControlMode mode) => mode switch
    {
        ExternalControlMode.User => "User",
        ExternalControlMode.ApprovalPending => "Approval pending",
        ExternalControlMode.Agent => "Agent",
        ExternalControlMode.UserTakeover => "User takeover",
        _ => mode.ToString(),
    };

    private enum ExternalControlMode
    {
        User,
        ApprovalPending,
        Agent,
        UserTakeover,
    }
}
