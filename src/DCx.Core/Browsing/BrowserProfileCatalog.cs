namespace DCx.Core.Browsing;

public static class BrowserProfileCatalog
{
    public static IReadOnlyList<string> BuiltInProfiles { get; } =
    [
        ProductIdentity.DefaultBrowserProfileName,
        "Work",
        "Private",
    ];

    public static bool IsBuiltIn(string? profileName) =>
        BuiltInProfiles.Contains(profileName, StringComparer.Ordinal);
}

