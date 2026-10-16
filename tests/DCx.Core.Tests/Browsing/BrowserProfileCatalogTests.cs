using DCx.Core.Browsing;

namespace DCx.Core.Tests.Browsing;

public sealed class BrowserProfileCatalogTests
{
    [Fact]
    public void BuiltInProfiles_AreDistinctAndIncludeDefault()
    {
        Assert.Contains("Default", BrowserProfileCatalog.BuiltInProfiles);
        Assert.Equal(
            BrowserProfileCatalog.BuiltInProfiles.Count,
            BrowserProfileCatalog.BuiltInProfiles.Distinct(StringComparer.Ordinal).Count());
    }
}

