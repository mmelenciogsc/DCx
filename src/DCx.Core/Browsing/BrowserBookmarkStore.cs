namespace DCx.Core.Browsing;

public sealed class BrowserBookmarkStore
{
    public const int MaximumEntries = 1000;
    private readonly AtomicJsonListStore<BrowserBookmark> _store;

    public BrowserBookmarkStore(string path)
    {
        _store = new AtomicJsonListStore<BrowserBookmark>(path);
    }

    public Task<IReadOnlyList<BrowserBookmark>> ReadAsync(CancellationToken cancellationToken = default) =>
        _store.ReadAsync(cancellationToken);

    public Task<IReadOnlyList<BrowserBookmark>> AddAsync(
        Uri address,
        string? title,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS addresses may be bookmarked.", nameof(address));
        }

        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? address.Host : title.Trim();
        var bookmark = new BrowserBookmark(address.AbsoluteUri, normalizedTitle, createdAtUtc.ToUniversalTime());
        return _store.UpdateAsync(entries =>
        {
            entries.RemoveAll(item => string.Equals(item.Address, bookmark.Address, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, bookmark);
            return entries.Take(MaximumEntries).ToList();
        }, cancellationToken);
    }
}

