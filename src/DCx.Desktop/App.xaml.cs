using System.Windows;
using DCx.Core.Reliability;
using DCx.Core.Storage;

namespace DCx.Desktop;

public partial class App : Application
{
    private ApplicationDataOperationLease? runtimeLease;

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
            runtimeLease = ApplicationDataOperationLease.Acquire(
                ApplicationDataRoot.Resolve(),
                "starting DCx");
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
        runtimeLease?.Dispose();
        base.OnExit(e);
    }
}
