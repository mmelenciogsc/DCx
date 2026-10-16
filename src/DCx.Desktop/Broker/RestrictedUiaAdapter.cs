using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace DCx.Desktop.Broker;

public sealed class RestrictedUiaAdapter(WindowsProcessBroker processBroker)
{
    private const int RestoreWindow = 9;

    public Task SetTextAsync(int processId, string automationId, string value) => Task.Run(() =>
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 4096)
        {
            throw new ArgumentException("UI Automation text is limited to 4096 characters.", nameof(value));
        }

        var element = FindElement(processId, automationId);
        var pattern = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
        pattern.SetValue(value);
    });

    public Task<string> ReadTextAsync(int processId, string automationId) => Task.Run(() =>
    {
        var element = FindElement(processId, automationId);
        var pattern = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
        return pattern.Current.Value;
    });

    public Task InvokeAsync(int processId, string automationId) => Task.Run(() =>
    {
        var element = FindElement(processId, automationId);
        var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
        pattern.Invoke();
    });

    public Task FocusAsync(int processId, string automationId) => Task.Run(() =>
    {
        var element = FindElement(processId, automationId);
        element.SetFocus();
    });

    public Task<bool> BringToForegroundAsync(int processId) => Task.Run(() =>
    {
        EnsureTracked(processId);
        using var process = Process.GetProcessById(processId);
        for (var attempt = 0; attempt < 20 && process.MainWindowHandle == IntPtr.Zero; attempt++)
        {
            Thread.Sleep(100);
            process.Refresh();
        }

        if (process.MainWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        ShowWindow(process.MainWindowHandle, RestoreWindow);
        return SetForegroundWindow(process.MainWindowHandle);
    });

    private AutomationElement FindElement(int processId, string automationId)
    {
        EnsureTracked(processId);
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        if (automationId.Length > 256)
        {
            throw new ArgumentException("The automation identifier is too long.", nameof(automationId));
        }
        var processCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, processId);
        var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, processCondition)
            ?? throw new InvalidOperationException("The tracked process has no accessible main window.");
        var idCondition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);
        var element = window.FindFirst(TreeScope.Descendants, idCondition)
            ?? throw new InvalidOperationException("The requested accessible control was not found in the tracked process.");
        return element;
    }

    private void EnsureTracked(int processId)
    {
        if (!processBroker.IsTrackedProcess(processId))
        {
            throw new UnauthorizedAccessException("UI Automation is limited to processes launched by this broker session.");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);
}
