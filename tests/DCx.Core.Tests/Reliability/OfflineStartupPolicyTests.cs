using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class OfflineStartupPolicyTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData(" TRUE ")]
    public void IsEnabled_AcceptsOnlyExplicitOptIn(string value) =>
        Assert.True(OfflineStartupPolicy.IsEnabled(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("yes")]
    public void IsEnabled_RejectsImplicitOrUnknownValues(string? value) =>
        Assert.False(OfflineStartupPolicy.IsEnabled(value));

    [Fact]
    public void OfflineDocument_IsAccessibleAndNetworkIndependent()
    {
        Assert.True(OfflineStartupPolicy.IsOfflineDocument("about:blank"));
        Assert.True(OfflineStartupPolicy.IsInitialInternalNavigation("data:text/html;charset=utf-8,fixture"));
        Assert.False(OfflineStartupPolicy.IsInitialInternalNavigation("data:application/javascript,alert(1)"));
        Assert.False(OfflineStartupPolicy.IsInitialInternalNavigation("https://example.com/"));
        Assert.Contains("<main>", OfflineStartupPolicy.DocumentHtml, StringComparison.Ordinal);
        Assert.Contains("<h1>DCx is ready offline</h1>", OfflineStartupPolicy.DocumentHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", OfflineStartupPolicy.DocumentHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", OfflineStartupPolicy.DocumentHtml, StringComparison.OrdinalIgnoreCase);
    }
}
