using Microsoft.Web.WebView2.Core;

namespace DCx.Desktop;

internal sealed record WebView2RuntimeStatus(bool IsAvailable, string? Version)
{
    public static WebView2RuntimeStatus Missing { get; } = new(false, null);
}

internal interface IWebView2RuntimeProbe
{
    WebView2RuntimeStatus Inspect();
}

internal sealed class InstalledWebView2RuntimeProbe : IWebView2RuntimeProbe
{
    public WebView2RuntimeStatus Inspect()
    {
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString(
                browserExecutableFolder: null,
                environmentOptions: CreateStableOptions());
            if (string.IsNullOrWhiteSpace(version)) return WebView2RuntimeStatus.Missing;
            RequireStableVersion(version);

            return new WebView2RuntimeStatus(true, version);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return WebView2RuntimeStatus.Missing;
        }
    }

    internal static CoreWebView2EnvironmentOptions CreateStableOptions() =>
        new() { ReleaseChannels = CoreWebView2ReleaseChannels.Stable };

    internal static void RequireStableVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl) ||
            !Version.TryParse(version, out var parsed) || parsed.Major < 1 || parsed.Minor < 0 ||
            parsed.Build < 0 || parsed.Revision < 0)
        {
            throw new InvalidOperationException("The stable WebView2 Runtime returned invalid version metadata.");
        }
    }
}

internal sealed class MissingWebView2RuntimeFixtureProbe : IWebView2RuntimeProbe
{
    public WebView2RuntimeStatus Inspect() => WebView2RuntimeStatus.Missing;
}
