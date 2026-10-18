using DCx.Core.Social;

namespace DCx.Core.Tests.Social;

public sealed class FacebookHistoricalPostSearchTests
{
    [Fact]
    public void ProductionSession_RequiresManualHandbackBeforeSearch()
    {
        var session = new FacebookHistoricalPostSearchSession(FacebookPostSearchSite.Production);

        Assert.Equal(FacebookHistoricalPostSearchState.AwaitingManualLogin, session.State);
        Assert.Equal("https://www.facebook.com/", session.ManualLoginAddress.AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => session.OpenSearch("taser cane"));

        session.ConfirmManualLoginHandback();
        var search = session.OpenSearch("  taser   cane  ");

        Assert.Equal("https://www.facebook.com/search/posts/?q=taser%20cane", search.AbsoluteUri);
        Assert.Equal(FacebookHistoricalPostSearchState.SearchOpened, session.State);
    }

    [Fact]
    public void Query_DeniesEmptyControlAndOversizedValues()
    {
        var site = FacebookPostSearchSite.Production;

        Assert.Throws<ArgumentException>(() => site.CreateSearchAddress(" "));
        Assert.Throws<ArgumentException>(() => site.CreateSearchAddress("post\nquery"));
        Assert.Throws<ArgumentException>(() => site.CreateSearchAddress("post\u202Equery"));
        Assert.Throws<ArgumentException>(() => site.CreateSearchAddress(new string('a', 201)));
    }

    [Fact]
    public void ControlledFixture_RequiresLiteralLoopbackRootHttpOrigin()
    {
        Assert.Throws<ArgumentException>(() =>
            FacebookPostSearchSite.CreateControlledFixture(new Uri("https://example.com/")));
        Assert.Throws<ArgumentException>(() =>
            FacebookPostSearchSite.CreateControlledFixture(new Uri("http://127.0.0.1:8080/path")));
        Assert.Throws<ArgumentException>(() =>
            FacebookPostSearchSite.CreateControlledFixture(new Uri("https://127.0.0.1:8080/")));
        Assert.Throws<ArgumentException>(() =>
            FacebookPostSearchSite.CreateControlledFixture(new Uri("http://localhost:8080/")));

        var site = FacebookPostSearchSite.CreateControlledFixture(new Uri("http://127.0.0.1:8080/"));
        Assert.True(site.IsControlledFixture);
        Assert.Equal("http://127.0.0.1:8080/social-login", site.LoginAddress.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:8080/social-posts?q=taser%20cane", site.CreateSearchAddress("taser cane").AbsoluteUri);
    }

    [Fact]
    public void Inspect_DeniesUnexpectedOriginWithoutReturningAddress()
    {
        var result = Assess(new Uri("https://lookalike-facebook.example/alice/posts/12345"));

        Assert.Equal(FacebookHistoricalPostSearchState.NoInspectedPost, result.State);
        Assert.Equal("unexpected_origin", result.Code);
        Assert.Null(result.DirectAddress);
    }

    [Fact]
    public void Inspect_LoginAndCheckpointNeverReturnAddress()
    {
        var login = Assess(
            new Uri("https://www.facebook.com/login/"),
            hasLoginPrompt: true);
        var checkpoint = Assess(
            new Uri("https://www.facebook.com/checkpoint/123"),
            hasAutomationBlock: true);

        Assert.Equal(FacebookHistoricalPostSearchState.ManualLoginRequired, login.State);
        Assert.Equal("manual_login_required", login.Code);
        Assert.Null(login.DirectAddress);
        Assert.Equal(FacebookHistoricalPostSearchState.SiteBlocked, checkpoint.State);
        Assert.Equal("site_blocked", checkpoint.Code);
        Assert.Null(checkpoint.DirectAddress);
    }

    [Fact]
    public void Inspect_SearchResultsPageCannotBecomeClaimedPost()
    {
        var result = Assess(new Uri("https://www.facebook.com/search/posts/?q=taser%20cane"));

        Assert.Equal(FacebookHistoricalPostSearchState.NoInspectedPost, result.State);
        Assert.Equal("no_inspected_post", result.Code);
        Assert.Null(result.DirectAddress);
    }

    [Fact]
    public void Inspect_DirectPostRequiresArticleEvidence()
    {
        var result = Assess(
            new Uri("https://www.facebook.com/alice/posts/pfbid12345"),
            hasArticle: false);

        Assert.Equal(FacebookHistoricalPostSearchState.NoInspectedPost, result.State);
        Assert.Null(result.DirectAddress);
    }

    [Fact]
    public void Inspect_VerifiesAndSanitizesCurrentDirectPostAddress()
    {
        var result = Assess(new Uri(
            "https://m.facebook.com/alice/posts/pfbid12345/?__cft__=private-tracking#comments"));

        Assert.True(result.HasVerifiedPost);
        Assert.Equal("inspected_post_verified", result.Code);
        Assert.Equal("https://www.facebook.com/alice/posts/pfbid12345", result.DirectAddress!.AbsoluteUri);
        Assert.Equal("Taser cane prototype", result.Heading);
        Assert.Equal("A bounded visible post excerpt.", result.Excerpt);
        Assert.Equal(new[] { "Prototype photo", "Demonstration video" }, result.MediaLabels);
    }

    [Fact]
    public void Inspect_VerifiesVideoPostPath()
    {
        var result = Assess(new Uri("https://www.facebook.com/alice/videos/123456789/"));

        Assert.True(result.HasVerifiedPost);
        Assert.Equal("https://www.facebook.com/alice/videos/123456789", result.DirectAddress!.AbsoluteUri);
    }

    [Fact]
    public void Inspect_VerifiesPermalinkAndDropsUnknownQueryValues()
    {
        var result = Assess(new Uri(
            "https://web.facebook.com/permalink.php?story_fbid=987654321&id=123456&tracking=secret"));

        Assert.True(result.HasVerifiedPost);
        Assert.Equal(
            "https://www.facebook.com/permalink.php?story_fbid=987654321&id=123456",
            result.DirectAddress!.AbsoluteUri);
    }

    [Fact]
    public void Inspect_DuplicateOrMissingPermalinkIdentityIsDenied()
    {
        var duplicate = Assess(new Uri(
            "https://www.facebook.com/permalink.php?story_fbid=1&story_fbid=2&id=3"));
        var missing = Assess(new Uri(
            "https://www.facebook.com/permalink.php?story_fbid=1"));

        Assert.Null(duplicate.DirectAddress);
        Assert.Null(missing.DirectAddress);
    }

    [Fact]
    public void Inspect_BoundsVisibleTextAndMediaWithoutPersistingInputs()
    {
        var labels = Enumerable.Range(0, 12).Select(index => $"Media {index}").ToArray();
        var result = Assess(
            new Uri("https://www.facebook.com/alice/posts/pfbid12345"),
            heading: new string('h', 400),
            articleText: new string('x', 2_000),
            mediaLabels: labels);

        Assert.Equal(300, result.Heading!.Length);
        Assert.Equal(1_500, result.Excerpt!.Length);
        Assert.Equal(8, result.MediaLabels.Count);
    }

    [Fact]
    public void Inspect_RemovesDirectionalControlsFromUntrustedVisibleText()
    {
        var result = Assess(
            new Uri("https://www.facebook.com/alice/posts/pfbid12345"),
            heading: "Safe\u202Etxt.exe",
            articleText: "Visible\0post",
            mediaLabels: ["Photo\u2066label"]);

        Assert.Equal("Safe txt.exe", result.Heading);
        Assert.Equal("Visible post", result.Excerpt);
        Assert.Equal("Photo label", Assert.Single(result.MediaLabels));
    }

    [Fact]
    public void ControlledFixture_VerifiesOnlyInspectedFragmentAndClearsBlockedResult()
    {
        var site = FacebookPostSearchSite.CreateControlledFixture(new Uri("http://127.0.0.1:8123/"));
        var session = new FacebookHistoricalPostSearchSession(site);
        session.ConfirmManualLoginHandback();
        session.OpenSearch("taser cane");

        var verified = session.Inspect(Evidence(
            new Uri("http://127.0.0.1:8123/social-posts?q=taser#taser-cane-2021")));
        var blocked = session.Inspect(Evidence(
            new Uri("http://127.0.0.1:8123/social-blocked"),
            hasAutomationBlock: true));

        Assert.Equal("http://127.0.0.1:8123/social-posts#taser-cane-2021", verified.DirectAddress!.AbsoluteUri);
        Assert.Null(blocked.DirectAddress);
        Assert.Empty(blocked.MediaLabels);
        Assert.Equal(FacebookHistoricalPostSearchState.SiteBlocked, session.State);
    }

    [Fact]
    public void ReportBlockedAndCancelNeverInventResult()
    {
        var session = new FacebookHistoricalPostSearchSession(FacebookPostSearchSite.Production);
        session.ConfirmManualLoginHandback();

        var blocked = session.ReportSiteBlocked();
        Assert.Null(blocked.DirectAddress);
        Assert.Equal("site_blocked", blocked.Code);

        session.Cancel();
        Assert.Throws<InvalidOperationException>(() => session.OpenSearch("taser cane"));
    }

    private static FacebookPostInspectionResult Assess(
        Uri address,
        bool hasArticle = true,
        bool hasLoginPrompt = false,
        bool hasAutomationBlock = false,
        string? heading = "Taser cane prototype",
        string? articleText = "A bounded visible post excerpt.",
        IReadOnlyList<string>? mediaLabels = null)
    {
        var session = new FacebookHistoricalPostSearchSession(FacebookPostSearchSite.Production);
        session.ConfirmManualLoginHandback();
        session.OpenSearch("taser cane");
        return session.Inspect(Evidence(
            address,
            hasArticle,
            hasLoginPrompt,
            hasAutomationBlock,
            heading,
            articleText,
            mediaLabels));
    }

    private static FacebookPostPageEvidence Evidence(
        Uri address,
        bool hasArticle = true,
        bool hasLoginPrompt = false,
        bool hasAutomationBlock = false,
        string? heading = "Taser cane prototype",
        string? articleText = "A bounded visible post excerpt.",
        IReadOnlyList<string>? mediaLabels = null) =>
        new(
            address,
            "Fixture title",
            heading,
            articleText,
            "June 14, 2021",
            "2021-06-14",
            mediaLabels ?? ["Prototype photo", "Demonstration video"],
            hasArticle,
            hasLoginPrompt,
            hasAutomationBlock,
            HasErrorPage: false);
}
