namespace DCx.Core.Navigation;

public sealed record NavigationDecision(bool IsAllowed, Uri? Uri, string? RejectionReason)
{
    public static NavigationDecision Allow(Uri uri) => new(true, uri, null);

    public static NavigationDecision Reject(string reason) => new(false, null, reason);
}

