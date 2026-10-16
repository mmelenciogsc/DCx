namespace DCx.Core.Navigation;

public sealed class AddressNavigationPolicy
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
    };

    public NavigationDecision Evaluate(string? address)
    {
        var candidate = address?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return NavigationDecision.Reject("Enter a web address.");
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return NavigationDecision.Reject("The address is not a valid absolute URL.");
        }

        if (!AllowedSchemes.Contains(uri.Scheme))
        {
            return NavigationDecision.Reject($"The {uri.Scheme} address type is not allowed in the web address bar.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return NavigationDecision.Reject("The address must include a host name.");
        }

        return NavigationDecision.Allow(uri);
    }
}

