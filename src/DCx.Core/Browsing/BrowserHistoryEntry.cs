namespace DCx.Core.Browsing;

public sealed record BrowserHistoryEntry(string Address, string Title, DateTimeOffset VisitedAtUtc)
{
    public override string ToString() => $"{Title} — {Address}";
}

