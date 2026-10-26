using System.Windows;

namespace DCx.PrerequisiteBootstrapper;

/// <summary>
/// Starts a separately signed-in-the-future, as-invoker review utility. It deliberately has no
/// browser, process, UAC, package-manager, PowerShell, download, network, or reboot composition.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var fixtureRequested = e.Args.Length == 1 &&
            string.Equals(e.Args[0], "--bootstrapper-fixture", StringComparison.Ordinal);
        if (fixtureRequested && !string.Equals(
                Environment.GetEnvironmentVariable("DCX_BOOTSTRAPPER_FIXTURE"),
                "1",
                StringComparison.Ordinal))
        {
            Shutdown(2);
            return;
        }

        if (e.Args.Length != 0 && !fixtureRequested)
        {
            Shutdown(2);
            return;
        }

        MainWindow = new BootstrapperWindow(
            fixtureRequested ? BootstrapperComposition.CreateFixture() : BootstrapperComposition.CreateNormal());
        MainWindow.Show();
    }
}
