using DCx.Core.Profiles;

namespace DCx.Core.Tests.Profiles;

public sealed class BrowserProfilePathsTests
{
    [Fact]
    public void GetProfileDirectory_StaysUnderConfiguredLocalDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dcx-path-test");
        var paths = new BrowserProfilePaths(root);

        var result = paths.GetProfileDirectory("Research");

        Assert.Equal(Path.Combine(root, "DCx", "WebView2", "Profiles", "Research"), result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("..")]
    public void GetProfileDirectory_RejectsUnsafeNames(string input)
    {
        var paths = new BrowserProfilePaths("C:\\safe-root");

        Assert.ThrowsAny<ArgumentException>(() => paths.GetProfileDirectory(input));
    }
}
