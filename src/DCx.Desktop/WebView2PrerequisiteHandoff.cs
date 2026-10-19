using System.Diagnostics;
using DCx.Core.Deployment;

namespace DCx.Desktop;

internal interface IWebView2PrerequisiteHandoff
{
    void OpenOfficialConsumerPage();
}

internal sealed class WindowsWebView2PrerequisiteHandoff : IWebView2PrerequisiteHandoff
{
    public void OpenOfficialConsumerPage()
    {
        var startInfo = new ProcessStartInfo(WebView2PrerequisiteHandoffPolicy.OfficialConsumerPage.AbsoluteUri)
        {
            UseShellExecute = true,
            Verb = string.Empty,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The default browser did not accept the official Microsoft page.");
    }
}

internal sealed class FixtureWebView2PrerequisiteHandoff : IWebView2PrerequisiteHandoff
{
    public void OpenOfficialConsumerPage()
    {
    }
}
