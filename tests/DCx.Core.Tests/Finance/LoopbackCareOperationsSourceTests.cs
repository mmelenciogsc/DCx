using System.Net;
using System.Text;
using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class LoopbackCareOperationsSourceTests
{
    private const string Token = "care-loopback-token-fixture-1234567890";
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
            new LoopbackCareOperationsSource(new Uri(value), Token));
    }

    [Fact]
    public async Task Refresh_RejectsUnknownResponseFieldsAndDoesNotExposeToken()
    {
        var handler = new StaticHandler(request => Json(
            request,
            """
            {"statusCode":"retry_due","pending":1,"due":1,"blocked":0,"completed":0,"maximumAttemptCount":0,"oldestPendingUtc":"2026-10-18T01:00:00+00:00","nextAttemptUtc":"2026-10-18T01:00:00+00:00","canProcessDue":true,"unexpected":"value"}
            """));
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
            source.RefreshAsync());

        Assert.Equal("care_operations_response_invalid", error.Code);
        Assert.DoesNotContain(Token, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refresh_RejectsResponseOriginSubstitution()
    {
        var handler = new StaticHandler(request =>
        {
            var response = Json(
                request,
                """
                {"statusCode":"clear","pending":0,"due":0,"blocked":0,"completed":0,"maximumAttemptCount":0,"oldestPendingUtc":null,"nextAttemptUtc":null,"canProcessDue":false}
                """);
            response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "http://127.0.0.1:5088/api/care/operations/health");
            return response;
        });
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
            source.RefreshAsync());

        Assert.Equal("care_operations_response_origin_invalid", error.Code);
    }

    [Fact]
    public async Task Process_RejectsNonzeroProviderCallResult()
    {
        var handler = new StaticHandler(request => Json(
            request,
            """
            {"scanned":1,"completed":1,"stillPending":0,"blocked":0,"providerCalls":1,"health":{"statusCode":"clear","pending":0,"due":0,"blocked":0,"completed":1,"maximumAttemptCount":1,"oldestPendingUtc":null,"nextAttemptUtc":null,"canProcessDue":false}}
            """));
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);
        var reviewed = new CareOperationsHealth(
            "retry_due",
            1,
            1,
            0,
            0,
            0,
            DateTimeOffset.Parse("2026-10-18T01:00:00Z"),
            DateTimeOffset.Parse("2026-10-18T01:00:00Z"),
            true);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
            source.ProcessDueAsync(reviewed, explicitlyApproved: true));

        Assert.Equal("care_operations_response_invalid", error.Code);
    }

    private static HttpResponseMessage Json(HttpRequestMessage request, string body) => new(HttpStatusCode.OK)
    {
        RequestMessage = request,
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StaticHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
