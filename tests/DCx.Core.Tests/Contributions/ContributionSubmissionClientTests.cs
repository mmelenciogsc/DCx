using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DCx.Core.Contributions;
using DCx.Core.Knowledge;

namespace DCx.Core.Tests.Contributions;

public sealed class ContributionSubmissionClientTests
{
    private const string Token = "contributor-token-fixture-1234567890";

    [Fact]
    public void ConstructorAndTransport_RequireHardenedRootLoopbackOrigin()
    {
        Assert.Throws<ArgumentException>(() =>
            new ContributionSubmissionClient(new Uri("https://example.test/")));
        Assert.Throws<ArgumentException>(() =>
            new ContributionSubmissionClient(new Uri("http://127.0.0.1:5087/base/")));
        Assert.Throws<ArgumentException>(() =>
            new ContributionSubmissionClient(new Uri("http://localhost:5087/")));
        using var handler = ContributionSubmissionClient.CreateHardenedHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(2, handler.MaxConnectionsPerServer);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
    }

    [Fact]
    public async Task Submit_UsesBoundedApprovedEnvelopeAndStrictAcceptedResponse()
    {
        var contribution = Prepared();
        var handler = new FixtureHandler(contribution.SubmissionId);
        using var client = new ContributionSubmissionClient(
            handler, new Uri("http://127.0.0.1:5087/"), TimeSpan.FromSeconds(5));

        var result = await client.SubmitAsync(contribution, Token);

        Assert.Equal(new ContributionSubmissionResult(contribution.SubmissionId, ContributionState.Quarantined), result);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal(Token, handler.Authorization?.Parameter);
        Assert.Equal(ContributionTransportCodec.MediaType, handler.MediaType);
        var decoded = ContributionTransportCodec.Decode(handler.RequestBody);
        Assert.Equal(contribution.SubmissionId, decoded.Submission.SubmissionId);
        Assert.Equal(
            ContributionCodec.CandidateSha256(contribution.Candidate),
            ContributionCodec.CandidateSha256(decoded.Submission.Candidate));
        Assert.Equal(contribution.Approval, decoded.Submission.Approval);
        Assert.Equal(contribution.State, decoded.Submission.State);
        Assert.Empty(decoded.Artifact);
    }

    [Fact]
    public async Task Submit_RejectsTokenOriginIdentityStateLocationAndUnknownResponse()
    {
        var contribution = Prepared();
        using (var invalidTokenClient = new ContributionSubmissionClient(
                   new FixtureHandler(contribution.SubmissionId), new Uri("http://127.0.0.1:5087/")))
        {
            var invalid = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
                invalidTokenClient.SubmitAsync(contribution, "short"));
            Assert.Equal("contributor_token_invalid", invalid.Code);
        }

        foreach (var handler in new[]
                 {
                     new FixtureHandler(contribution.SubmissionId) { EscapeResponseOrigin = true },
                     new FixtureHandler("submission:other"),
                     new FixtureHandler(contribution.SubmissionId) { State = ContributionState.Approved },
                     new FixtureHandler(contribution.SubmissionId) { WrongLocation = true },
                     new FixtureHandler(contribution.SubmissionId) { AddUnknownResponseField = true },
                     new FixtureHandler(contribution.SubmissionId) { DuplicateResponseField = true },
                     new FixtureHandler(contribution.SubmissionId) { WrongResponsePath = true },
                     new FixtureHandler(contribution.SubmissionId) { WrongContentType = true },
                     new FixtureHandler(contribution.SubmissionId) { InvalidCharset = true },
                     new FixtureHandler(contribution.SubmissionId) { ContentEncoding = true },
                     new FixtureHandler(contribution.SubmissionId) { DeclaredOversizedResponse = true },
                     new FixtureHandler(contribution.SubmissionId) { OversizedResponse = true },
                 })
        {
            using var client = new ContributionSubmissionClient(
                handler, new Uri("http://127.0.0.1:5087/"), TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
                client.SubmitAsync(contribution, Token));
        }
    }

    [Fact]
    public async Task Submit_ReturnsOnlyStableServerErrorCode()
    {
        var contribution = Prepared();
        using var client = new ContributionSubmissionClient(
            new FixtureHandler(contribution.SubmissionId)
            {
                StatusCode = HttpStatusCode.Unauthorized,
                ErrorCode = "authentication_invalid",
            },
            new Uri("http://127.0.0.1:5087/"));

        var exception = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
            client.SubmitAsync(contribution, Token));
        Assert.Equal("authentication_invalid", exception.Code);

        foreach (var handler in new[]
                 {
                     new FixtureHandler(contribution.SubmissionId)
                     {
                         StatusCode = HttpStatusCode.Unauthorized,
                         ErrorCode = "authentication_invalid",
                         WrongContentType = true,
                     },
                     new FixtureHandler(contribution.SubmissionId)
                     {
                         StatusCode = HttpStatusCode.Unauthorized,
                         ErrorCode = "authentication_invalid",
                         DuplicateResponseField = true,
                     },
                 })
        {
            using var invalidClient = new ContributionSubmissionClient(
                handler, new Uri("http://127.0.0.1:5087/"));
            var invalid = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
                invalidClient.SubmitAsync(contribution, Token));
            Assert.Contains(
                invalid.Code,
                new[] { "submission_response_invalid", "submission_rejected" });
        }
    }

    [Fact]
    public async Task Status_BindsMinimalOwnerResponseToExactPreparedItem()
    {
        var contribution = Prepared();
        var quarantinedHandler = new LifecycleHandler(contribution);
        using (var client = new ContributionSubmissionClient(
                   quarantinedHandler, new Uri("http://127.0.0.1:5087/")))
        {
            var status = await client.GetStatusAsync(contribution, Token);
            Assert.Equal(ContributionState.Quarantined, status.State);
            Assert.Null(status.ReviewedUtc);
            Assert.Null(status.ReleaseSequence);
            Assert.Equal(HttpMethod.Get, quarantinedHandler.Method);
            Assert.Equal(
                $"/api/contributions/{Uri.EscapeDataString(contribution.SubmissionId)}",
                quarantinedHandler.Path);
            Assert.Equal(Token, quarantinedHandler.Authorization?.Parameter);
        }

        var reviewedUtc = DateTimeOffset.Parse("2026-10-17T00:01:00Z");
        var approvedHandler = new LifecycleHandler(contribution)
        {
            State = ContributionState.Approved,
            ReviewedUtc = reviewedUtc,
            ReleaseSequence = 7,
        };
        using var approvedClient = new ContributionSubmissionClient(
            approvedHandler, new Uri("http://127.0.0.1:5087/"));
        var approved = await approvedClient.GetStatusAsync(contribution, Token);
        Assert.Equal(ContributionState.Approved, approved.State);
        Assert.Equal(reviewedUtc, approved.ReviewedUtc);
        Assert.Equal(7, approved.ReleaseSequence);
    }

    [Fact]
    public async Task Status_RejectsUnboundMalformedOrExpandedResponses()
    {
        var contribution = Prepared();
        foreach (var handler in new[]
                 {
                     new LifecycleHandler(contribution) { WrongSubmissionId = true },
                     new LifecycleHandler(contribution) { WrongCandidateDigest = true },
                     new LifecycleHandler(contribution) { State = ContributionState.Approved },
                     new LifecycleHandler(contribution) { AddUnknownResponseField = true },
                     new LifecycleHandler(contribution) { DuplicateResponseField = true },
                     new LifecycleHandler(contribution) { WrongContentType = true },
                     new LifecycleHandler(contribution) { InvalidCharset = true },
                     new LifecycleHandler(contribution) { ContentEncoding = true },
                     new LifecycleHandler(contribution) { EscapeResponseOrigin = true },
                     new LifecycleHandler(contribution) { WrongResponsePath = true },
                     new LifecycleHandler(contribution) { DeclaredOversizedResponse = true },
                     new LifecycleHandler(contribution) { OversizedResponse = true },
                 })
        {
            using var client = new ContributionSubmissionClient(
                handler, new Uri("http://127.0.0.1:5087/"));
            var exception = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
                client.GetStatusAsync(contribution, Token));
            Assert.Contains(
                exception.Code,
                new[] { "submission_response_invalid", "submission_response_origin_invalid" });
        }
    }

    [Fact]
    public async Task Withdrawal_RequiresExactWithdrawnResponseAndReleaseBinding()
    {
        var contribution = Prepared();
        var handler = new LifecycleHandler(contribution)
        {
            ReturnWithdrawal = true,
            State = ContributionState.Withdrawn,
        };
        using var client = new ContributionSubmissionClient(
            handler, new Uri("http://127.0.0.1:5087/"));

        var result = await client.WithdrawAsync(contribution, Token);

        Assert.Equal(ContributionState.Withdrawn, result.State);
        Assert.Equal(contribution.SubmissionId, result.SubmissionId);
        Assert.Equal(7, result.Release.Sequence);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            $"/api/contributions/{Uri.EscapeDataString(contribution.SubmissionId)}/withdraw",
            handler.Path);
    }

    [Fact]
    public async Task Withdrawal_RejectsWrongIdentityStateReleaseAndServerError()
    {
        var contribution = Prepared();
        foreach (var handler in new[]
                 {
                     new LifecycleHandler(contribution)
                     {
                         ReturnWithdrawal = true,
                         State = ContributionState.Withdrawn,
                         WrongSubmissionId = true,
                     },
                     new LifecycleHandler(contribution)
                     {
                         ReturnWithdrawal = true,
                         State = ContributionState.Withdrawn,
                         WrongCandidateDigest = true,
                     },
                     new LifecycleHandler(contribution)
                     {
                         ReturnWithdrawal = true,
                         State = ContributionState.Approved,
                     },
                     new LifecycleHandler(contribution)
                     {
                         ReturnWithdrawal = true,
                         State = ContributionState.Withdrawn,
                         InvalidRelease = true,
                     },
                     new LifecycleHandler(contribution)
                     {
                         ReturnWithdrawal = true,
                         State = ContributionState.Withdrawn,
                         AddUnknownResponseField = true,
                     },
                 })
        {
            using var client = new ContributionSubmissionClient(
                handler, new Uri("http://127.0.0.1:5087/"));
            var exception = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
                client.WithdrawAsync(contribution, Token));
            Assert.Equal("submission_response_invalid", exception.Code);
        }

        using var rejectedClient = new ContributionSubmissionClient(
            new LifecycleHandler(contribution)
            {
                ReturnWithdrawal = true,
                StatusCode = HttpStatusCode.Conflict,
                ErrorCode = "submission_not_approved",
            },
            new Uri("http://127.0.0.1:5087/"));
        var rejected = await Assert.ThrowsAsync<ContributionSubmissionException>(() =>
            rejectedClient.WithdrawAsync(contribution, Token));
        Assert.Equal("submission_not_approved", rejected.Code);
    }

    [Fact]
    public async Task StatusAndWithdrawal_PropagateCancellation()
    {
        var contribution = Prepared();
        var statusHandler = new CancellationHandler();
        using (var client = new ContributionSubmissionClient(
                   statusHandler, new Uri("http://127.0.0.1:5087/")))
        using (var cancellation = new CancellationTokenSource())
        {
            var operation = client.GetStatusAsync(contribution, Token, cancellation.Token);
            await statusHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }

        var withdrawalHandler = new CancellationHandler();
        using (var client = new ContributionSubmissionClient(
                   withdrawalHandler, new Uri("http://127.0.0.1:5087/")))
        using (var cancellation = new CancellationTokenSource())
        {
            var operation = client.WithdrawAsync(contribution, Token, cancellation.Token);
            await withdrawalHandler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
    }

    private static PreparedContribution Prepared()
    {
        var candidate = new ContributionCandidate(
            "candidate:client-fixture",
            "resource:client-fixture",
            "1.0.0",
            KnowledgeResourceKind.Ckb,
            "Client fixture",
            "Reviewed deterministic contribution.",
            "urn:dcx:client-fixture",
            "CC-BY-4.0",
            [],
            [],
            "contributor:client-fixture");
        var now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
        return new ContributionPreparationService(new ContributionScanner()).Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            new ContributionItemApproval(
                candidate.CandidateId,
                ContributionCodec.CandidateSha256(candidate),
                "approval-client-fixture-1234",
                now),
            now);
    }

    private sealed class FixtureHandler(string submissionId) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public string? MediaType { get; private set; }
        public byte[] RequestBody { get; private set; } = [];
        public bool EscapeResponseOrigin { get; init; }
        public bool WrongLocation { get; init; }
        public bool AddUnknownResponseField { get; init; }
        public bool DuplicateResponseField { get; init; }
        public bool WrongResponsePath { get; init; }
        public bool WrongContentType { get; init; }
        public bool InvalidCharset { get; init; }
        public bool ContentEncoding { get; init; }
        public bool DeclaredOversizedResponse { get; init; }
        public bool OversizedResponse { get; init; }
        public ContributionState State { get; init; } = ContributionState.Quarantined;
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.Accepted;
        public string ErrorCode { get; init; } = "submission_rejected";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var response = new HttpResponseMessage(StatusCode)
            {
                RequestMessage = EscapeResponseOrigin
                    ? new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:6553/api/contributions/intake")
                    : WrongResponsePath
                        ? new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:5087/api/contributions/other")
                    : request,
            };
            if (StatusCode == HttpStatusCode.Accepted)
            {
                var unknown = AddUnknownResponseField ? ",\"unknown\":true" : string.Empty;
                var duplicate = DuplicateResponseField
                    ? $",\"state\":{(int)State}"
                    : string.Empty;
                var body = OversizedResponse
                    ? new string('x', 16 * 1024 + 1)
                    : $"{{\"submissionId\":\"{submissionId}\",\"state\":{(int)State}{duplicate}{unknown}}}";
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                response.Headers.Location = new Uri(
                    WrongLocation
                        ? "/api/contributions/submission%3Awrong"
                        : $"/api/contributions/{Uri.EscapeDataString(submissionId)}",
                    UriKind.Relative);
            }
            else
            {
                var duplicate = DuplicateResponseField ? $",\"code\":\"{ErrorCode}\"" : string.Empty;
                response.Content = new StringContent(
                    $"{{\"code\":\"{ErrorCode}\"{duplicate}}}", Encoding.UTF8, "application/json");
            }

            if (WrongContentType)
            {
                response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            }
            else if (InvalidCharset)
            {
                response.Content.Headers.ContentType!.CharSet = "utf-16";
            }
            if (ContentEncoding) response.Content.Headers.ContentEncoding.Add("gzip");
            if (DeclaredOversizedResponse) response.Content.Headers.ContentLength = 16 * 1024 + 1;

            return response;
        }
    }

    private sealed class LifecycleHandler(PreparedContribution contribution) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public bool ReturnWithdrawal { get; init; }
        public bool WrongSubmissionId { get; init; }
        public bool WrongCandidateDigest { get; init; }
        public bool AddUnknownResponseField { get; init; }
        public bool DuplicateResponseField { get; init; }
        public bool WrongContentType { get; init; }
        public bool InvalidCharset { get; init; }
        public bool ContentEncoding { get; init; }
        public bool EscapeResponseOrigin { get; init; }
        public bool WrongResponsePath { get; init; }
        public bool DeclaredOversizedResponse { get; init; }
        public bool OversizedResponse { get; init; }
        public bool InvalidRelease { get; init; }
        public ContributionState State { get; init; } = ContributionState.Quarantined;
        public DateTimeOffset? ReviewedUtc { get; init; }
        public long? ReleaseSequence { get; init; }
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string ErrorCode { get; init; } = "submission_rejected";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization;
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            var response = new HttpResponseMessage(StatusCode)
            {
                RequestMessage = EscapeResponseOrigin
                    ? new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:6553/api/contributions/escaped")
                    : WrongResponsePath
                        ? new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:5087/api/contributions/other")
                    : request,
            };
            if (StatusCode != HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    $"{{\"code\":\"{ErrorCode}\"}}", Encoding.UTF8, "application/json");
                return Task.FromResult(response);
            }

            var submissionId = WrongSubmissionId ? "submission:other" : contribution.SubmissionId;
            var candidateSha256 = WrongCandidateDigest
                ? new string('0', 64)
                : ContributionCodec.CandidateSha256(contribution.Candidate);
            var unknown = AddUnknownResponseField ? ",\"unknown\":true" : string.Empty;
            var duplicate = DuplicateResponseField ? $",\"state\":{(int)State}" : string.Empty;
            string body;
            if (OversizedResponse)
            {
                body = new string('x', 16 * 1024 + 1);
            }
            else if (ReturnWithdrawal)
            {
                var releaseSha256 = InvalidRelease ? "invalid" : new string('a', 64);
                var releaseSize = InvalidRelease ? 0 : 1234;
                body =
                    $"{{\"submissionId\":\"{submissionId}\",\"candidateSha256\":\"{candidateSha256}\",\"state\":{(int)State},\"release\":{{\"sequence\":7,\"sha256\":\"{releaseSha256}\",\"sizeBytes\":{releaseSize}}}{duplicate}{unknown}}}";
            }
            else
            {
                var created = DateTimeOffset.Parse("2026-10-17T00:00:00Z").ToString("O");
                var reviewed = ReviewedUtc is null ? "null" : $"\"{ReviewedUtc.Value:O}\"";
                var sequence = ReleaseSequence?.ToString() ?? "null";
                body =
                    $"{{\"submissionId\":\"{submissionId}\",\"candidateSha256\":\"{candidateSha256}\",\"state\":{(int)State},\"createdUtc\":\"{created}\",\"reviewedUtc\":{reviewed},\"releaseSequence\":{sequence}{duplicate}{unknown}}}";
            }

            response.Content = new StringContent(
                body,
                Encoding.UTF8,
                WrongContentType ? "text/plain" : "application/json");
            if (InvalidCharset) response.Content.Headers.ContentType!.CharSet = "utf-16";
            if (ContentEncoding) response.Content.Headers.ContentEncoding.Add("gzip");
            if (DeclaredOversizedResponse) response.Content.Headers.ContentLength = 16 * 1024 + 1;
            return Task.FromResult(response);
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation fixture continued unexpectedly.");
        }
    }
}
