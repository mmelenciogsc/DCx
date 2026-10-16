namespace DCx.Core.Browsing;

public sealed class BrowserHistoryStore
{
    public const int MaximumEntries = 2000;
    private readonly AtomicJsonListStore<BrowserHistoryEntry> _store;

    public BrowserHistoryStore(string path)
    {
        _store = new AtomicJsonListStore<BrowserHistoryEntry>(path);
    }

    public Task<IReadOnlyList<BrowserHistoryEntry>> ReadAsync(CancellationToken cancellationToken = default) =>
        _store.ReadAsync(cancellationToken);

    public Task<IReadOnlyList<BrowserHistoryEntry>> RecordAsync(
        Uri address,
        string? title,
        DateTimeOffset visitedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateWebAddress(address);
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? address.Host : title.Trim();
        var entry = new BrowserHistoryEntry(address.AbsoluteUri, normalizedTitle, visitedAtUtc.ToUniversalTime());

        return _store.UpdateAsync(entries =>
        {
            if (entries.FirstOrDefault()?.Address == entry.Address)
            {
                entries.RemoveAt(0);
            }

            entries.Insert(0, entry);
            return entries.Take(MaximumEntries).ToList();
        }, cancellationToken);
    }

    private static void ValidateWebAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS addresses may be stored.", nameof(address));
        }
    }
}

