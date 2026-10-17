using System.Windows;
using System.Windows.Automation;
using DCx.Core.Reliability;

namespace DCx.Desktop;

public partial class CrashDiagnosticsWindow : Window
{
    private readonly LocalCrashReportStore? store;
    private readonly CancellationTokenSource lifetime = new();

    public CrashDiagnosticsWindow(LocalCrashReportStore? store)
    {
        this.store = store;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        SetControlsEnabled(false);
        try
        {
            await store.SetConsentAsync(
                ConsentBox.IsChecked == true,
                LocalCrashReportStore.CurrentNoticeVersion,
                DateTimeOffset.UtcNow,
                lifetime.Token);
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The local crash diagnostics setting could not be saved. No data was sent.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (store is null) return;
        SetControlsEnabled(false);
        try
        {
            await store.PurgeReportsAsync(lifetime.Token);
            var status = await store.GetStatusAsync(lifetime.Token);
            ConsentBox.IsChecked = status.Consent.Enabled;
            SetStatus(status.Consent.Enabled
                ? "All retained crash reports were deleted. Local crash diagnostics remain enabled. Nothing was sent."
                : "All retained crash reports were deleted. Local crash diagnostics remain disabled. Nothing was sent.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("Retained crash reports could not be deleted. No data was sent.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        if (store is null)
        {
            ConsentBox.IsChecked = false;
            ConsentBox.IsEnabled = false;
            SaveButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            SetStatus("Local crash diagnostics are unavailable. No crash report will be retained or sent.");
            return;
        }

        try
        {
            var status = await store.GetStatusAsync(lifetime.Token);
            ConsentBox.IsChecked = status.Consent.Enabled;
            SetStatus(status.Consent.Enabled
                ? $"Local crash diagnostics are enabled. {status.RetainedReportCount} of {LocalCrashReportStore.MaximumRetainedReports} crash reports are retained. Nothing is sent automatically."
                : "Local crash diagnostics are disabled. No crash reports are retained or sent.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetControlsEnabled(false);
            SetStatus("Local crash diagnostics status could not be read. No data was sent.");
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        if (store is null) enabled = false;
        ConsentBox.IsEnabled = enabled;
        SaveButton.IsEnabled = enabled;
        DeleteButton.IsEnabled = enabled;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }
}
