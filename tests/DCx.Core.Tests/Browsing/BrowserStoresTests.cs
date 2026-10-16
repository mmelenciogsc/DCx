using DCx.Core.Browsing;

namespace DCx.Core.Tests.Browsing;

public sealed class BrowserStoresTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dcx-store-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task History_RoundTripsNewestFirstAndDeduplicatesConsecutiveAddress()
    {
        var store = new BrowserHistoryStore(Path.Combine(_directory, "history.json"));
        var first = new Uri("https://example.com/first");
        var second = new Uri("https://example.com/second");

        await store.RecordAsync(first, "First", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await store.RecordAsync(second, "Second", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        await store.RecordAsync(second, "Second updated", DateTimeOffset.Parse("2026-01-03T00:00:00Z"));

        var entries = await store.ReadAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(second.AbsoluteUri, entries[0].Address);
        Assert.Equal("Second updated", entries[0].Title);
        Assert.Equal(first.AbsoluteUri, entries[1].Address);
    }

    [Fact]
    public async Task Bookmarks_RoundTripAndReplaceMatchingAddress()
    {
        var store = new BrowserBookmarkStore(Path.Combine(_directory, "bookmarks.json"));
        var address = new Uri("https://example.com/");

        await store.AddAsync(address, "Old", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        await store.AddAsync(address, "New", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        var entries = await store.ReadAsync();
        var bookmark = Assert.Single(entries);
        Assert.Equal("New", bookmark.Title);
    }

    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("data:text/plain,test")]
    public async Task Stores_RejectNonWebAddresses(string rawAddress)
    {
        var address = new Uri(rawAddress);
        var history = new BrowserHistoryStore(Path.Combine(_directory, "history.json"));
        var bookmarks = new BrowserBookmarkStore(Path.Combine(_directory, "bookmarks.json"));

        await Assert.ThrowsAsync<ArgumentException>(() => history.RecordAsync(address, "Bad", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => bookmarks.AddAsync(address, "Bad", DateTimeOffset.UtcNow));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

