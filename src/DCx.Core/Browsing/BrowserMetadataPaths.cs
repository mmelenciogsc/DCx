using DCx.Core.Storage;

namespace DCx.Core.Browsing;

public sealed class BrowserMetadataPaths
{
    public BrowserMetadataPaths(string? localApplicationData = null)
    {
        var root = ApplicationDataRoot.Resolve(localApplicationData);
        BrowserDataDirectory = Path.Combine(root, "Browser");
    }

    public string BrowserDataDirectory { get; }

    public string HistoryFile => Path.Combine(BrowserDataDirectory, "history.json");

    public string BookmarksFile => Path.Combine(BrowserDataDirectory, "bookmarks.json");
}
