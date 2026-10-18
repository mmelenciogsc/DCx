using System.Net;
using System.Text;
using System.Text.Json;
using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class LoopbackCareContributorDashboardSourceTests
{
    private const string Token = "care-contributor-token-fixture-1234567890";
    private const string Contributor = "contributor:loopback-fixture";
    private static readonly Uri Origin = new("http://127.0.0.1:5087/");

    [Theory]
    [InlineData("https://127.0.0.1:5087/")]
    [InlineData("http://localhost:5087/")]
    [InlineData("http://127.0.0.1:5087/path")]
    [InlineData("http://127.0.0.1:5087/?query=1")]
    [InlineData("http://user@127.0.0.1:5087/")]
    public void Constructor_RequiresLiteralLoopbackRootHttpOrigin(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            new LoopbackCareContributorDashboardSource(
                new Uri(value), Token, Contributor));
    }

    [Fact]
    public async Task Load_BindsAuthorizationOriginSubjectAndNoStoreRequest()
    {
        var handler = new StaticHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/care/contributor-dashboard",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(Token, request.Headers.Authorization.Parameter);
            Assert.True(request.Headers.CacheControl!.NoStore);
            Assert.Contains(
                request.Headers.Accept,
                value => value.MediaType == "application/json");
            return Json(request, Snapshot());
        });
        using var source = new LoopbackCareContributorDashboardSource(
            handler, Origin, Token, Contributor);

        var snapshot = await source.LoadAsync();

        Assert.Equal(Contributor, snapshot.ContributorPseudonym);
        Assert.Empty(snapshot.Periods);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("subject")]
    [InlineData("duplicate")]
    public async Task Load_RejectsUnknownFieldsAndSubjectSubstitution(string caseName)
    {
        var body = caseName switch
        {
            "unknown" => """
                         {"statusCode":"available","contributorPseudonym":"contributor:loopback-fixture","currency":"USD","generatedUtc":"2026-10-18T12:00:00+00:00","periods":[],"providerAccount":"must-not-cross-boundary"}
                         """,
            "subject" => """
                         {"statusCode":"available","contributorPseudonym":"contributor:substituted","currency":"USD","generatedUtc":"2026-10-18T12:00:00+00:00","periods":[]}
                         """,
            _ => """
                 {"statusCode":"available","statusCode":"not_configured","contributorPseudonym":"contributor:loopback-fixture","currency":"USD","generatedUtc":"2026-10-18T12:00:00+00:00","periods":[]}
                 """,
        };
        var handler = new StaticHandler(request => Json(request, body));
        using var source = new LoopbackCareContributorDashboardSource(
            handler, Origin, Token, Contributor);

        var error = await Assert.ThrowsAsync<CareContributorDashboardTransportException>(
            () => source.LoadAsync());

        Assert.Equal("care_contributor_response_invalid", error.Code);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_RejectsResponseOriginSubstitution()
    {
        var handler = new StaticHandler(request =>
        {
            var response = Json(request, Snapshot());
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get,
                "http://127.0.0.1:5088/api/care/contributor-dashboard");
            return response;
        });
        using var source = new LoopbackCareContributorDashboardSource(
            handler, Origin, Token, Contributor);

        var error = await Assert.ThrowsAsync<CareContributorDashboardTransportException>(
            () => source.LoadAsync());

        Assert.Equal("care_contributor_response_origin_invalid", error.Code);
    }

    [Fact]
    public async Task Load_RejectsOversizedResponseBeforeJsonParsing()
    {
        var handler = new StaticHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(
                new string('x', 64 * 1024 + 1),
                Encoding.UTF8,
                "application/json"),
        });
        using var source = new LoopbackCareContributorDashboardSource(
            handler, Origin, Token, Contributor);

        var error = await Assert.ThrowsAsync<CareContributorDashboardTransportException>(
            () => source.LoadAsync());

        Assert.Equal("care_contributor_response_invalid", error.Code);
    }

    [Fact]
    public async Task Load_ReturnsOnlyBoundedStableServerErrorCode()
    {
        var handler = new StaticHandler(request => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            RequestMessage = request,
            Content = new StringContent(
                "{\"code\":\"authentication_invalid\"}",
                Encoding.UTF8,
                "application/json"),
        });
        using var source = new LoopbackCareContributorDashboardSource(
            handler, Origin, Token, Contributor);

        var error = await Assert.ThrowsAsync<CareContributorDashboardTransportException>(
            () => source.LoadAsync());

        Assert.Equal("authentication_invalid", error.Code);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
    }

    private static CareContributorDashboardSnapshot Snapshot() => new(
        CareContributorDashboardPolicy.Available,
        Contributor,
        "USD",
        new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero),
        []);

    private static HttpResponseMessage Json(
        HttpRequestMessage request,
        CareContributorDashboardSnapshot snapshot) =>
        Json(
            request,
            JsonSerializer.Serialize(
                snapshot,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static HttpResponseMessage Json(HttpRequestMessage request, string body) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StaticHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
