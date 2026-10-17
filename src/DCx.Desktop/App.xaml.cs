using System.Windows;
using System.Windows.Threading;
using DCx.Core.Reliability;
using DCx.Core.Storage;

namespace DCx.Desktop;

public partial class App : Application
{
    private ApplicationDataOperationLease? runtimeLease;
    private LocalCrashReportStore? crashReportStore;
    private bool crashHandlersRegistered;

    internal LocalCrashReportStore? CrashReportStore => crashReportStore;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--recovery", StringComparison.Ordinal))
        {
            MainWindow = new RecoveryWindow();
            MainWindow.Show();
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--readiness", StringComparison.Ordinal))
        {
            MainWindow = new ReadinessWindow();
            MainWindow.Show();
            return;
        }

        try
        {
            var applicationDataRoot = ApplicationDataRoot.Resolve();
            runtimeLease = ApplicationDataOperationLease.Acquire(applicationDataRoot, "starting DCx");
            TryInitializeCrashDiagnostics(applicationDataRoot);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (ApplicationDataInUseException exception)
        {
            MessageBox.Show(
                exception.Message,
                "DCx data is in use",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterCrashHandlers();
        runtimeLease?.Dispose();
        base.OnExit(e);
    }

    private void TryInitializeCrashDiagnostics(string applicationDataRoot)
    {
        try
        {
            crashReportStore = LocalCrashReportStore.OpenAsync(applicationDataRoot).GetAwaiter().GetResult();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            crashHandlersRegistered = true;
        }
        catch
        {
            // Optional local diagnostics must never prevent the browser from starting.
            crashReportStore = null;
        }
    }

    private void UnregisterCrashHandlers()
    {
        if (!crashHandlersRegistered) return;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        crashHandlersRegistered = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        TryRecordException(e.Exception, CrashComponent.UserInterface, processTerminating: false);

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TryRecordException(exception, CrashComponent.ProcessTermination, e.IsTerminating);
        }
        else
        {
            TryRecordCategory(CrashComponent.ProcessTermination, CrashErrorCategory.Unknown, e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        TryRecordException(e.Exception, CrashComponent.BackgroundTask, processTerminating: false);

    private void TryRecordException(Exception exception, CrashComponent component, bool processTerminating)
    {
        try
        {
            crashReportStore?.RecordExceptionAsync(
                    exception,
                    component,
                    processTerminating,
                    GetApplicationVersion(),
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Crash recording is best effort and must not alter exception handling.
        }
    }

    private void TryRecordCategory(
        CrashComponent component,
        CrashErrorCategory category,
        bool processTerminating)
    {
        try
        {
            crashReportStore?.RecordAsync(
                    component,
                    category,
                    processTerminating,
                    GetApplicationVersion(),
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Crash recording is best effort and must not alter exception handling.
        }
    }

    private static string GetApplicationVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
