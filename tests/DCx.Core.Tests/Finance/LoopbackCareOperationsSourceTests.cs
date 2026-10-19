using System.Net;
using System.Text;
using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class LoopbackCareOperationsSourceTests
{
    private const string Token = "care-loopback-token-fixture-1234567890";
    private static readonly Uri Origin = new("http://127.0.0.1:5087/");

    [Fact]
    public void Handler_BoundsHeadersConnectionsAndAmbientHttpFeatures()
    {
        using var handler = LoopbackCareOperationsSource.CreateHardenedHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(2, handler.MaxConnectionsPerServer);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }

    [Fact]
    public async Task Refresh_SendsPrivacyHeadersAndAcceptsStrictResponse()
    {
        var handler = new StaticHandler(request =>
        {
            Assert.True(request.Headers.CacheControl!.NoStore);
            Assert.Contains(request.Headers.Accept, item => item.MediaType == "application/json");
            Assert.Contains(request.Headers.AcceptEncoding, item => item.Value == "identity");
            return Json(request, ClearHealth());
        });
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);
        Assert.Equal("clear", (await source.RefreshAsync()).StatusCode);
    }

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
    public async Task Refresh_RejectsDuplicateChangedRouteAndInvalidJsonContentContracts()
    {
        var duplicate = ClearHealth().Replace(
            "\"statusCode\":\"clear\"",
            "\"statusCode\":\"clear\",\"statusCode\":\"clear\"",
            StringComparison.Ordinal);
        var cases = new (string Body, Action<HttpResponseMessage>? Mutate)[]
        {
            (duplicate, null),
            (ClearHealth(), response => response.RequestMessage = new HttpRequestMessage(
                HttpMethod.Get, "http://127.0.0.1:5087/api/care/operations/other")),
            (ClearHealth(), response => response.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse("text/plain")),
            (ClearHealth(), response => response.Content.Headers.ContentType!.CharSet = "utf-16"),
            (ClearHealth(), response => response.Content.Headers.ContentEncoding.Add("gzip")),
            (ClearHealth(), response => response.Content.Headers.ContentLength = 64 * 1024 + 1),
        };
        foreach (var item in cases)
        {
            var handler = new StaticHandler(request =>
            {
                var response = Json(request, item.Body);
                item.Mutate?.Invoke(response);
                return response;
            });
            using var source = new LoopbackCareOperationsSource(handler, Origin, Token);
            var error = await Assert.ThrowsAsync<CareOperationsTransportException>(
                () => source.RefreshAsync());
            Assert.Contains(
                error.Code,
                new[] { "care_operations_response_invalid", "care_operations_response_origin_invalid" });
        }
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

    [Fact]
    public async Task BlockedCaseList_RejectsIdentifierBearingUnknownFields()
    {
        var handler = new StaticHandler(request => Json(
            request,
            """
            [{"caseId":"0123456789abcdef0123456789abcdef","eventKind":"refund","reasonCode":"policy_rejected","attemptCount":1,"firstReceivedUtc":"2026-10-18T00:58:00+00:00","lastAttemptUtc":"2026-10-18T00:59:00+00:00","reviewSha256":"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789","dispositionCode":"unreviewed","eventId":"must-not-cross-boundary"}]
            """));
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
            source.ListBlockedCasesAsync());

        Assert.Equal("care_operations_response_invalid", error.Code);
    }

    [Fact]
    public async Task BlockedCaseDisposition_RejectsFinancialOrProviderEffects()
    {
        var handler = new StaticHandler(request => Json(
            request,
            """
            {"caseId":"0123456789abcdef0123456789abcdef","dispositionCode":"retained_blocked","recordedUtc":"2026-10-18T01:00:00+00:00","providerCalls":0,"financialMutations":1}
            """));
        using var source = new LoopbackCareOperationsSource(handler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(() =>
            source.RecordDispositionAsync(new CareBlockedCaseDispositionRequest(
                "0123456789abcdef0123456789abcdef",
                "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                CareBlockedCaseReviewPolicy.RetainedBlocked,
                ExplicitlyApproved: true)));

        Assert.Equal("care_blocked_case_response_invalid", error.Code);
    }

    [Fact]
    public async Task FailedPayoutList_AcceptsMinimizedResponseAndRejectsSensitiveUnknownField()
    {
        const string minimized =
            """
            [{"reviewId":"0123456789abcdef0123456789abcdef","carePeriodId":"care:2026-05","amountCents":700,"failureCode":"provider_rejected","holdCode":"failed_item_hold","noticeCode":"not_ready","failedAtUtc":"2026-10-18T00:40:00+00:00","reviewSha256":"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789","dispositionCode":"unreviewed"}]
            """;
        var acceptedHandler = new StaticHandler(request => Json(request, minimized));
        using (var source = new LoopbackCareOperationsSource(
                   acceptedHandler, Origin, Token))
        {
            var review = Assert.Single(await source.ListFailedPayoutReviewsAsync());
            Assert.Equal("0123456789abcdef0123456789abcdef", review.ReviewId);
            Assert.Equal(700, review.AmountCents);
        }

        var sensitive = minimized.Replace(
            "\"dispositionCode\":\"unreviewed\"",
            "\"dispositionCode\":\"unreviewed\",\"contributorPseudonym\":\"must-not-cross\"",
            StringComparison.Ordinal);
        var deniedHandler = new StaticHandler(request => Json(request, sensitive));
        using var deniedSource = new LoopbackCareOperationsSource(
            deniedHandler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(
            () => deniedSource.ListFailedPayoutReviewsAsync());

        Assert.Equal("care_operations_response_invalid", error.Code);
    }

    [Fact]
    public async Task FailedPayoutDisposition_BindsExactRouteAndRejectsAuthorityEffects()
    {
        var request = new CareFailedPayoutDispositionRequest(
            "0123456789abcdef0123456789abcdef",
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            ExplicitlyApproved: true);
        var acceptedHandler = new StaticHandler(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(
                "/api/care/operations/failed-payout-holds/0123456789abcdef0123456789abcdef/disposition",
                message.RequestUri!.AbsolutePath);
            var body = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.DoesNotContain("contributor", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(request.ReviewedSha256, body, StringComparison.Ordinal);
            return Json(
                message,
                """
                {"reviewId":"0123456789abcdef0123456789abcdef","dispositionCode":"request_recipient_update","noticeCode":"local_dashboard_notice_ready","recordedUtc":"2026-10-18T01:02:00+00:00","providerCalls":0,"financialMutations":0,"payoutAttempts":0,"notificationTransports":0}
                """);
        });
        using (var source = new LoopbackCareOperationsSource(
                   acceptedHandler, Origin, Token))
        {
            var result = await source.RecordFailedPayoutDispositionAsync(request);
            Assert.Equal(CareFailedPayoutReviewPolicy.RequestRecipientUpdate, result.DispositionCode);
            Assert.Equal(0, result.ProviderCalls);
            Assert.Equal(0, result.FinancialMutations);
            Assert.Equal(0, result.PayoutAttempts);
            Assert.Equal(0, result.NotificationTransports);
        }

        var deniedHandler = new StaticHandler(message => Json(
            message,
            """
            {"reviewId":"0123456789abcdef0123456789abcdef","dispositionCode":"request_recipient_update","noticeCode":"local_dashboard_notice_ready","recordedUtc":"2026-10-18T01:02:00+00:00","providerCalls":0,"financialMutations":0,"payoutAttempts":1,"notificationTransports":0}
            """));
        using var deniedSource = new LoopbackCareOperationsSource(
            deniedHandler, Origin, Token);

        var error = await Assert.ThrowsAsync<CareOperationsTransportException>(
            () => deniedSource.RecordFailedPayoutDispositionAsync(request));

        Assert.Equal("care_failed_payout_response_invalid", error.Code);
    }

    private static string ClearHealth() =>
        """
        {"statusCode":"clear","pending":0,"due":0,"blocked":0,"completed":0,"maximumAttemptCount":0,"oldestPendingUtc":null,"nextAttemptUtc":null,"canProcessDue":false}
        """;

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
