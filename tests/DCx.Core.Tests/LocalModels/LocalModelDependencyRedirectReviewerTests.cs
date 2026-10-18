using System.Net;
using System.Security.Cryptography;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelDependencyRedirectReviewerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExplicitReview_UsesHeadOnlyAndReturnsExactQueryFreeSessionPolicy()
    {
        var fixture = CreateFixture();
        var target = new Uri("https://assets.example.test/releases/dependency.bin?signature=secret-value");
        var handler = new FixtureHandler(request =>
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? Redirect(request, target)
                : request.RequestUri == target
                    ? Final(request, fixture.PayloadItem.ExpectedBytes)
                    : Final(request, fixture.LicenseItem.ExpectedBytes));
        var clock = new MutableTimeProvider(Now);
        using var reviewer = new LocalModelDependencyRedirectReviewer(
            handler,
            fixture.Catalog,
            clock,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30));

        var session = await reviewer.ReviewEmbeddedAsync(
            fixture.Plan,
            LocalModelDependencyRedirectReviewApproval.Create(fixture.Plan, approved: true));

        Assert.True(session.IsCurrent);
        Assert.Equal(fixture.Plan.PlanSha256, session.PlanSha256);
        Assert.Equal(2, session.ItemCount);
        Assert.Equal(3, session.RequestCount);
        Assert.Equal(1, session.RedirectCount);
        Assert.Equal(2, session.ExactLengthCount);
        Assert.Equal(Now, session.ObservedAtUtc);
        Assert.Equal(Now.AddMinutes(30), session.ExpiresAtUtc);
        Assert.True(session.PolicyReview.AllowsRedirects);
        var route = Assert.Single(session.PolicyReview.Routes);
        Assert.Equal(fixture.PayloadItem.SourceUri, route.FromUriWithoutQuery);
        Assert.Equal("https://assets.example.test/releases/dependency.bin", route.TargetUriWithoutQuery.AbsoluteUri);
        Assert.DoesNotContain('?', route.TargetUriWithoutQuery.AbsoluteUri);
        Assert.True(session.PolicyReview.IsAllowed(
            fixture.PayloadItem,
            fixture.PayloadItem.SourceUri,
            target,
            redirectNumber: 1));
        Assert.False(session.PolicyReview.IsAllowed(
            fixture.PayloadItem,
            fixture.PayloadItem.SourceUri,
            new Uri("https://assets.example.test/releases/other.bin?signature=secret-value"),
            redirectNumber: 1));
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Head, request.Method));
        Assert.All(handler.Requests, request => Assert.Equal("identity", Assert.Single(request.AcceptEncoding)));
        Assert.All(handler.Requests, request => Assert.True(request.NoStore));
        Assert.All(handler.Requests, request => Assert.False(request.Authorization));
        Assert.All(handler.Requests, request => Assert.False(request.Cookie));
        Assert.All(handler.Requests, request => Assert.False(request.Referrer));
        Assert.Equal(0, handler.ResponseBodiesRead);
    }

    [Fact]
    public async Task MissingApprovalAndChangedCatalogFailBeforeNetwork()
    {
        var fixture = CreateFixture();
        var handler = new FixtureHandler(request => Final(request, 1));
        using var reviewer = new LocalModelDependencyRedirectReviewer(
            handler,
            fixture.Catalog,
            new MutableTimeProvider(Now),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30));

        await AssertCodeAsync("dependency_redirect_review_approval_invalid", () =>
            reviewer.ReviewEmbeddedAsync(
                fixture.Plan,
                LocalModelDependencyRedirectReviewApproval.Create(fixture.Plan, approved: false)));

        var changedCatalog = fixture.Catalog with { CatalogSha256 = new string('f', 64) };
        var changedPlan = new LocalModelDependencyTransferPlanner().CreateForAll(
            changedCatalog,
            long.MaxValue);
        await AssertCodeAsync("dependency_redirect_review_catalog_invalid", () =>
            reviewer.ReviewEmbeddedAsync(
                changedPlan,
                LocalModelDependencyRedirectReviewApproval.Create(changedPlan, approved: true)));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnsafeLoopAndMismatchedLengthFailClosedWithoutReadingBodies()
    {
        var fixture = CreateFixture();
        var unsafeHandler = new FixtureHandler(request =>
            Redirect(request, new Uri("http://127.0.0.1/private.bin")));
        using (var reviewer = Reviewer(unsafeHandler, fixture))
        {
            await AssertCodeAsync("dependency_redirect_review_redirect_invalid", () => Review(reviewer, fixture));
            Assert.Single(unsafeHandler.Requests);
            Assert.Equal(0, unsafeHandler.ResponseBodiesRead);
        }

        var loopTarget = new Uri("https://assets.example.test/releases/dependency.bin?one=secret");
        var loopHandler = new FixtureHandler(request =>
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? Redirect(request, loopTarget)
                : Redirect(request, fixture.PayloadItem.SourceUri));
        using (var reviewer = Reviewer(loopHandler, fixture))
        {
            await AssertCodeAsync("dependency_redirect_review_redirect_invalid", () => Review(reviewer, fixture));
            Assert.Equal(2, loopHandler.Requests.Count);
            Assert.Equal(0, loopHandler.ResponseBodiesRead);
        }

        var lengthHandler = new FixtureHandler(request => Final(
            request,
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? fixture.PayloadItem.ExpectedBytes + 1
                : fixture.LicenseItem.ExpectedBytes));
        using (var reviewer = Reviewer(lengthHandler, fixture))
        {
            await AssertCodeAsync("dependency_redirect_review_response_invalid", () => Review(reviewer, fixture));
            Assert.Single(lengthHandler.Requests);
            Assert.Equal(0, lengthHandler.ResponseBodiesRead);
        }
    }

    [Fact]
    public async Task SessionExpiryRevokesPolicyAndRejectsCoordinatorConstruction()
    {
        var fixture = CreateFixture();
        var handler = new FixtureHandler(request => Final(
            request,
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? fixture.PayloadItem.ExpectedBytes
                : fixture.LicenseItem.ExpectedBytes));
        var clock = new MutableTimeProvider(Now);
        using var reviewer = new LocalModelDependencyRedirectReviewer(
            handler,
            fixture.Catalog,
            clock,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30));
        var session = await Review(reviewer, fixture);

        Assert.True(session.IsCurrent);
        Assert.Equal(session.PolicyReview.PolicySha256, session.CreatePolicy().Review.PolicySha256);
        clock.UtcNow = session.ExpiresAtUtc;
        Assert.False(session.IsCurrent);
        Assert.Equal(
            LocalModelDependencyRedirectPolicyReview.DenyAll.PolicySha256,
            session.CreatePolicy().Review.PolicySha256);
        Assert.Throws<ArgumentException>(() =>
            new LocalModelDependencyTransferSetCoordinator(
                Path.Combine(Path.GetTempPath(), "dcx-expired-session-fixture"),
                session));
    }

    [Fact]
    public async Task ReviewedSession_ComposesWithTransferAndExpiryDeniesBeforeNewPayloadRequest()
    {
        var fixture = CreateFixture();
        var observedTarget = new Uri("https://assets.example.test/releases/dependency.bin?signature=observed-secret");
        var clock = new MutableTimeProvider(Now);
        var reviewHandler = new FixtureHandler(request =>
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? Redirect(request, observedTarget)
                : request.RequestUri == observedTarget
                    ? Final(request, fixture.PayloadItem.ExpectedBytes)
                    : Final(request, fixture.LicenseItem.ExpectedBytes));
        using var reviewer = new LocalModelDependencyRedirectReviewer(
            reviewHandler,
            fixture.Catalog,
            clock,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30));
        var session = await Review(reviewer, fixture);

        var payload = "redirect review payload"u8.ToArray();
        var transferTarget = new Uri("https://assets.example.test/releases/dependency.bin?signature=fresh-secret");
        var transferHandler = new TransferFixtureHandler(request =>
            request.RequestUri == fixture.PayloadItem.SourceUri
                ? Redirect(request, transferTarget)
                : TransferResponse(request, payload));
        using var transfer = new LocalModelDependencyHttpTransfer(
            transferHandler,
            fixture.Catalog,
            redirectPolicy: session.CreatePolicy());
        using var destination = new MemoryStream();
        var approval = LocalModelDependencyTransferApproval.Create(
            fixture.Plan,
            fixture.PayloadItem,
            session.PolicyReview,
            approved: true);

        var result = await transfer.TransferAsync(fixture.Plan, approval, destination);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal(session.PolicyReview.PolicySha256, result.RedirectPolicySha256);
        Assert.Equal([fixture.PayloadItem.SourceUri, transferTarget], transferHandler.RequestedUris);

        clock.UtcNow = session.ExpiresAtUtc;
        var requestsBeforeExpiryDenial = transferHandler.RequestedUris.Count;
        await AssertTransferCodeAsync("dependency_transfer_approval_invalid", () =>
            transfer.TransferAsync(fixture.Plan, approval, new MemoryStream()));
        Assert.Equal(requestsBeforeExpiryDenial, transferHandler.RequestedUris.Count);
    }

    [Fact]
    public async Task CallerCancellationAndDeadlineUseStableOutcomes()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using (var reviewer = Reviewer(new NeverCompletesHandler(), fixture))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reviewer.ReviewEmbeddedAsync(
                fixture.Plan,
                LocalModelDependencyRedirectReviewApproval.Create(fixture.Plan, approved: true),
                cancellation.Token));
        }

        using var timeoutReviewer = new LocalModelDependencyRedirectReviewer(
            new NeverCompletesHandler(),
            fixture.Catalog,
            new MutableTimeProvider(Now),
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMinutes(30));
        await AssertCodeAsync("dependency_redirect_review_timeout", () => Review(timeoutReviewer, fixture));
    }

    private static LocalModelDependencyRedirectReviewer Reviewer(
        HttpMessageHandler handler,
        Fixture fixture) => new(
            handler,
            fixture.Catalog,
            new MutableTimeProvider(Now),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMinutes(30));

    private static Task<LocalModelDependencyRedirectReviewSession> Review(
        LocalModelDependencyRedirectReviewer reviewer,
        Fixture fixture) => reviewer.ReviewEmbeddedAsync(
            fixture.Plan,
            LocalModelDependencyRedirectReviewApproval.Create(fixture.Plan, approved: true));

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<LocalModelDependencyRedirectReviewException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static async Task AssertTransferCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static Fixture CreateFixture()
    {
        var payload = "redirect review payload"u8.ToArray();
        var license = "x"u8.ToArray();
        var candidate = new PinnedLocalModelDependencyCandidate(
            "review-fixture",
            LocalModelDependencyKind.WhisperModel,
            "Redirect review fixture",
            new Uri("https://github.com/dcx/dependency.bin"),
            payload.LongLength,
            Digest(payload),
            new LocalModelDependencyLicense(
                "MIT",
                new Uri("https://raw.githubusercontent.com/dcx/LICENSE"),
                license.LongLength,
                Digest(license),
                false),
            []);
        var catalog = new LocalModelDependencyCatalogReview(
            LocalModelDependencyCatalogLoader.CurrentSchemaVersion,
            "redirect-review-fixture",
            new string('a', 64),
            Array.AsReadOnly([candidate]));
        var plan = new LocalModelDependencyTransferPlanner().CreateForAll(catalog, long.MaxValue);
        var payloadItem = Assert.Single(plan.Items, item => item.SourceUri == candidate.SourceUri);
        var licenseItem = Assert.Single(plan.Items, item => item.SourceUri == candidate.License.SourceUri);
        return new Fixture(catalog, plan, payloadItem, licenseItem);
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static HttpResponseMessage Redirect(HttpRequestMessage request, Uri target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
            Content = new TrackingContent(),
        };
        response.Headers.Location = target;
        return response;
    }

    private static HttpResponseMessage Final(HttpRequestMessage request, long contentLength)
    {
        var content = new TrackingContent();
        content.Headers.ContentLength = contentLength;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
            Content = content,
        };
    }

    private static HttpResponseMessage TransferResponse(HttpRequestMessage request, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = bytes.LongLength;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
            Content = content,
        };
    }

    private sealed record Fixture(
        LocalModelDependencyCatalogReview Catalog,
        LocalModelDependencyTransferPlan Plan,
        LocalModelDependencyTransferItem PayloadItem,
        LocalModelDependencyTransferItem LicenseItem);

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RequestObservation> Requests { get; } = [];
        public int ResponseBodiesRead => TrackingContent.ReadCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestObservation(
                request.Method,
                request.RequestUri!,
                request.Headers.AcceptEncoding.Select(value => value.Value).ToArray(),
                request.Headers.CacheControl?.NoStore == true,
                request.Headers.Authorization is not null,
                request.Headers.Contains("Cookie"),
                request.Headers.Referrer is not null));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RequestObservation(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyList<string> AcceptEncoding,
        bool NoStore,
        bool Authorization,
        bool Cookie,
        bool Referrer);

    private sealed class TrackingContent : ByteArrayContent
    {
        public static int ReadCount { get; private set; }

        public TrackingContent() : base([])
        {
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            ReadCount++;
            return base.CreateContentReadStreamAsync();
        }
    }

    private sealed class NeverCompletesHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class TransferFixtureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUris.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
