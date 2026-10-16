namespace DCx.Core.Browsing;

public sealed record BrowserBookmark(string Address, string Title, DateTimeOffset CreatedAtUtc)
{
    public override string ToString() => $"{Title} — {Address}";
}

