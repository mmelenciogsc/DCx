using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DCx.Core.Social;

public enum FacebookHistoricalPostSearchState
{
    AwaitingManualLogin,
    ReadyToSearch,
    SearchOpened,
    VerifiedPost,
    ManualLoginRequired,
    SiteBlocked,
    NoInspectedPost,
    Cancelled,
}

public sealed record FacebookPostPageEvidence(
    Uri Address,
    string? DocumentTitle,
    string? ArticleHeading,
    string? ArticleText,
    string? PublishedText,
    string? PublishedDateTime,
    IReadOnlyList<string> MediaLabels,
    bool HasArticle,
    bool HasLoginPrompt,
    bool HasAutomationBlock,
    bool HasErrorPage);

public sealed record FacebookPostInspectionResult(
    FacebookHistoricalPostSearchState State,
    string Code,
    Uri? DirectAddress,
    string? Heading,
    string? Excerpt,
    string? PublishedText,
    string? PublishedDateTime,
    IReadOnlyList<string> MediaLabels)
{
    public bool HasVerifiedPost => State == FacebookHistoricalPostSearchState.VerifiedPost;
}

public sealed class FacebookPostSearchSite
{
    private static readonly Uri FacebookOrigin = new("https://www.facebook.com/");

    private FacebookPostSearchSite(Uri origin, Uri loginAddress, bool isControlledFixture)
    {
        Origin = origin;
        LoginAddress = loginAddress;
        IsControlledFixture = isControlledFixture;
    }

    public Uri Origin { get; }

    public Uri LoginAddress { get; }

    public bool IsControlledFixture { get; }

    public static FacebookPostSearchSite Production { get; } =
        new(FacebookOrigin, FacebookOrigin, false);

    public static FacebookPostSearchSite CreateControlledFixture(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            !origin.IsLoopback ||
            !IPAddress.TryParse(origin.Host, out var loopbackAddress) ||
            !IPAddress.IsLoopback(loopbackAddress) ||
            origin.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment) ||
            origin.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "A controlled Facebook-search fixture must use a root literal-loopback HTTP origin.",
                nameof(origin));
        }

        return new FacebookPostSearchSite(origin, new Uri(origin, "social-login"), true);
    }

    public Uri CreateSearchAddress(string query)
    {
        var normalized = FacebookHistoricalPostSearchPolicy.NormalizeQuery(query);
        var escaped = Uri.EscapeDataString(normalized);
        return IsControlledFixture
            ? new Uri(Origin, $"social-posts?q={escaped}")
            : new Uri(Origin, $"search/posts/?q={escaped}");
    }

    internal bool IsAllowedAddress(Uri address)
    {
        if (IsControlledFixture)
        {
            return address.Scheme == Origin.Scheme &&
                   address.Host == Origin.Host &&
                   address.Port == Origin.Port &&
                   string.IsNullOrEmpty(address.UserInfo);
        }

        return address.Scheme == Uri.UriSchemeHttps &&
               address.IsDefaultPort &&
               string.IsNullOrEmpty(address.UserInfo) &&
               address.Host is "facebook.com" or "www.facebook.com" or "m.facebook.com" or "web.facebook.com";
    }
}

public sealed class FacebookHistoricalPostSearchSession
{
    private readonly FacebookHistoricalPostSearchPolicy _policy;

    public FacebookHistoricalPostSearchSession(FacebookPostSearchSite site)
    {
        Site = site ?? throw new ArgumentNullException(nameof(site));
        _policy = new FacebookHistoricalPostSearchPolicy(site);
        State = FacebookHistoricalPostSearchState.AwaitingManualLogin;
    }

    public FacebookPostSearchSite Site { get; }

    public FacebookHistoricalPostSearchState State { get; private set; }

    public Uri ManualLoginAddress => Site.LoginAddress;

    public void ConfirmManualLoginHandback()
    {
        EnsureNotCancelled();
        State = FacebookHistoricalPostSearchState.ReadyToSearch;
    }

    public Uri OpenSearch(string query)
    {
        EnsureNotCancelled();
        if (State is not (FacebookHistoricalPostSearchState.ReadyToSearch or
            FacebookHistoricalPostSearchState.SearchOpened or
            FacebookHistoricalPostSearchState.NoInspectedPost or
            FacebookHistoricalPostSearchState.SiteBlocked or
            FacebookHistoricalPostSearchState.ManualLoginRequired or
            FacebookHistoricalPostSearchState.VerifiedPost))
        {
            throw new InvalidOperationException("Manual browser control must be handed back before search navigation.");
        }

        var address = Site.CreateSearchAddress(query);
        State = FacebookHistoricalPostSearchState.SearchOpened;
        return address;
    }

    public FacebookPostInspectionResult Inspect(FacebookPostPageEvidence evidence)
    {
        EnsureNotCancelled();
        if (State == FacebookHistoricalPostSearchState.AwaitingManualLogin)
        {
            throw new InvalidOperationException("Manual browser control must be handed back before inspection.");
        }

        var result = _policy.Assess(evidence);
        State = result.State;
        return result;
    }

    public FacebookPostInspectionResult ReportSiteBlocked()
    {
        EnsureNotCancelled();
        State = FacebookHistoricalPostSearchState.SiteBlocked;
        return FacebookHistoricalPostSearchPolicy.Empty(
            FacebookHistoricalPostSearchState.SiteBlocked,
            "site_blocked");
    }

    public void Cancel()
    {
        State = FacebookHistoricalPostSearchState.Cancelled;
    }

    private void EnsureNotCancelled()
    {
        if (State == FacebookHistoricalPostSearchState.Cancelled)
        {
            throw new InvalidOperationException("The historical-post search session was cancelled.");
        }
    }
}

public sealed partial class FacebookHistoricalPostSearchPolicy(FacebookPostSearchSite site)
{
    private const int MaximumQueryLength = 200;
    private const int MaximumTitleLength = 300;
    private const int MaximumExcerptLength = 1_500;
    private const int MaximumPublishedLength = 128;
    private const int MaximumMediaLabelLength = 300;
    private const int MaximumMediaLabels = 8;

    private readonly FacebookPostSearchSite _site = site ?? throw new ArgumentNullException(nameof(site));

    public FacebookPostInspectionResult Assess(FacebookPostPageEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Address);
        ArgumentNullException.ThrowIfNull(evidence.MediaLabels);

        if (!_site.IsAllowedAddress(evidence.Address))
        {
            return Empty(FacebookHistoricalPostSearchState.NoInspectedPost, "unexpected_origin");
        }

        if (evidence.HasAutomationBlock || evidence.HasErrorPage || IsCheckpointAddress(evidence.Address))
        {
            return Empty(FacebookHistoricalPostSearchState.SiteBlocked, "site_blocked");
        }

        if (evidence.HasLoginPrompt || IsLoginAddress(evidence.Address))
        {
            return Empty(FacebookHistoricalPostSearchState.ManualLoginRequired, "manual_login_required");
        }

        var directAddress = _site.IsControlledFixture
            ? TryNormalizeFixturePostAddress(evidence.Address)
            : TryNormalizeFacebookPostAddress(evidence.Address);
        if (directAddress is null || !evidence.HasArticle)
        {
            return Empty(FacebookHistoricalPostSearchState.NoInspectedPost, "no_inspected_post");
        }

        var heading = NormalizeOptional(evidence.ArticleHeading, MaximumTitleLength) ??
                      NormalizeOptional(evidence.DocumentTitle, MaximumTitleLength);
        var excerpt = NormalizeOptional(evidence.ArticleText, MaximumExcerptLength);
        if (heading is null && excerpt is null)
        {
            return Empty(FacebookHistoricalPostSearchState.NoInspectedPost, "post_evidence_missing");
        }

        var media = evidence.MediaLabels
            .Select(value => NormalizeOptional(value, MaximumMediaLabelLength))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumMediaLabels)
            .ToArray();

        return new FacebookPostInspectionResult(
            FacebookHistoricalPostSearchState.VerifiedPost,
            "inspected_post_verified",
            directAddress,
            heading,
            excerpt,
            NormalizeOptional(evidence.PublishedText, MaximumPublishedLength),
            NormalizeOptional(evidence.PublishedDateTime, MaximumPublishedLength),
            new ReadOnlyCollection<string>(media));
    }

    internal static string NormalizeQuery(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Any(value => char.IsControl(value) || IsUnsafeDirectionalControl(value)))
        {
            throw new ArgumentException("The search query contains control characters.", nameof(query));
        }

        var normalized = WhitespaceRegex().Replace(query.Trim(), " ");
        if (normalized.Length is 0 or > MaximumQueryLength)
        {
            throw new ArgumentException("The search query must contain 1 to 200 characters.", nameof(query));
        }

        return normalized;
    }

    internal static FacebookPostInspectionResult Empty(
        FacebookHistoricalPostSearchState state,
        string code) =>
        new(state, code, null, null, null, null, null, Array.Empty<string>());

    private bool IsLoginAddress(Uri address) =>
        !_site.IsControlledFixture &&
        address.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase);

    private bool IsCheckpointAddress(Uri address) =>
        !_site.IsControlledFixture &&
        (address.AbsolutePath.StartsWith("/checkpoint", StringComparison.OrdinalIgnoreCase) ||
         address.AbsolutePath.StartsWith("/recover", StringComparison.OrdinalIgnoreCase));

    private Uri? TryNormalizeFixturePostAddress(Uri address)
    {
        if (address.AbsolutePath != "/social-posts" ||
            !FixtureFragmentRegex().IsMatch(address.Fragment))
        {
            return null;
        }

        return new UriBuilder(address.Scheme, address.Host, address.Port, address.AbsolutePath)
        {
            Fragment = address.Fragment[1..],
        }.Uri;
    }

    private static Uri? TryNormalizeFacebookPostAddress(Uri address)
    {
        var pathMatch = DirectPostPathRegex().Match(address.AbsolutePath);
        if (pathMatch.Success)
        {
            return new UriBuilder(Uri.UriSchemeHttps, "www.facebook.com", -1,
                $"/{pathMatch.Groups[1].Value}/{pathMatch.Groups[2].Value}/{pathMatch.Groups[3].Value}").Uri;
        }

        if (!string.Equals(address.AbsolutePath, "/permalink.php", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var values = ParseQuery(address.Query);
        if (!values.TryGetValue("story_fbid", out var story) ||
            !values.TryGetValue("id", out var id) ||
            !FacebookIdRegex().IsMatch(story) ||
            !FacebookIdRegex().IsMatch(id))
        {
            return null;
        }

        return new Uri(
            $"https://www.facebook.com/permalink.php?story_fbid={Uri.EscapeDataString(story)}&id={Uri.EscapeDataString(id)}");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            if (key is "story_fbid" or "id")
            {
                if (!values.TryAdd(key, value))
                {
                    return [];
                }
            }
        }

        return values;
    }

    private static string? NormalizeOptional(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var safe = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            safe.Append(char.IsControl(character) || IsUnsafeDirectionalControl(character)
                ? ' '
                : character);
        }

        var normalized = WhitespaceRegex().Replace(safe.ToString().Trim(), " ");
        return normalized.Length <= maximumLength ? normalized : normalized[..maximumLength];
    }

    private static bool IsUnsafeDirectionalControl(char value) =>
        value is '\u200E' or '\u200F' or
            >= '\u202A' and <= '\u202E' or
            >= '\u2066' and <= '\u2069';

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^#[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex FixtureFragmentRegex();

    [GeneratedRegex(@"^/([A-Za-z0-9._-]{1,100})/(posts|videos)/([A-Za-z0-9_-]{5,128})/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex DirectPostPathRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex FacebookIdRegex();
}
