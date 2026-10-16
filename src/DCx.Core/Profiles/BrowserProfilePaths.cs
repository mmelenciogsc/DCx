using DCx.Core.Storage;

namespace DCx.Core.Profiles;

public sealed class BrowserProfilePaths
{
    private readonly string _localApplicationData;

    public BrowserProfilePaths(string? localApplicationData = null)
    {
        _localApplicationData = ApplicationDataRoot.Resolve(localApplicationData);
    }

    public string GetProfileDirectory(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var invalidCharacters = Path.GetInvalidFileNameChars();
        if (profileName.IndexOfAny(invalidCharacters) >= 0 || profileName is "." or "..")
        {
            throw new ArgumentException("The profile name contains invalid characters.", nameof(profileName));
        }

        return Path.Combine(
            _localApplicationData,
            "WebView2",
            "Profiles",
            profileName);
    }
}
