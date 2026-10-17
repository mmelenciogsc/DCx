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
                    : request,
            };
            if (StatusCode == HttpStatusCode.Accepted)
            {
                var unknown = AddUnknownResponseField ? ",\"unknown\":true" : string.Empty;
                var body = OversizedResponse
                    ? new string('x', 16 * 1024 + 1)
                    : $"{{\"submissionId\":\"{submissionId}\",\"state\":{(int)State}{unknown}}}";
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
                response.Headers.Location = new Uri(
                    WrongLocation
                        ? "/api/contributions/submission%3Awrong"
                        : $"/api/contributions/{Uri.EscapeDataString(submissionId)}",
                    UriKind.Relative);
            }
            else
            {
                response.Content = new StringContent(
                    $"{{\"code\":\"{ErrorCode}\"}}", Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
