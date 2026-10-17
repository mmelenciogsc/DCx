using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCx.Core.Contributions;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;

namespace DCx.Core.Tests.Contributions;

public sealed class ContributionApiE2eTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-p10-api-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoopbackApi_EnforcesRolesPropagatesApprovedItemAndNeverLogsSecrets()
    {
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        var keyPath = Path.Combine(root, "simulation-key.pk8");
        await File.WriteAllBytesAsync(keyPath, privateKey);
        var contributorToken = $"contributor-{Guid.NewGuid():N}";
        var reviewerToken = $"reviewer-{Guid.NewGuid():N}";
        var wrongToken = $"wrong-{Guid.NewGuid():N}";
        var dataRoot = Path.Combine(root, "server");
        var endpoint = new Uri($"http://127.0.0.1:{ReserveEphemeralPort()}/");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(GlobalRegistryRepository).Assembly.Location);
        startInfo.Environment["DCX_GLOBAL_SIMULATION"] = "1";
        startInfo.Environment["DCX_GLOBAL_DATA_ROOT"] = dataRoot;
        startInfo.Environment["DCX_GLOBAL_SIGNING_KEY"] = keyPath;
        startInfo.Environment["DCX_GLOBAL_SIMULATION_URL"] = endpoint.AbsoluteUri;
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_API"] = "1";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_WORKER"] = "1";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_WORKER_INTERVAL_MS"] = "100";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256"] = Digest(contributorToken);
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM"] = "contributor:api-fixture";
        startInfo.Environment["DCX_GLOBAL_REVIEWER_TOKEN_SHA256"] = Digest(reviewerToken);
        startInfo.Environment["DCX_GLOBAL_REVIEWER_ID"] = "reviewer:api-fixture";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the contribution API fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var privateBody = "A deterministic accessible contribution sent through the authenticated loopback fixture.";
        var riskyBody = "hello world " + "sk-" + "abcdefghijklmnopqrstuvwxyz123456" + " someone@example.test";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await WaitForHealthAsync(client, endpoint, process, errorTask);
            using (var health = JsonDocument.Parse(
                       await client.GetStringAsync(new Uri(endpoint, "health"))))
            {
                Assert.True(health.RootElement.GetProperty("contributionApi").GetBoolean());
                Assert.True(health.RootElement.GetProperty("contributionRateLimit").GetBoolean());
                Assert.True(health.RootElement.GetProperty("contributionWorker").GetBoolean());
                Assert.False(health.RootElement.GetProperty("ccbSandbox").GetBoolean());
            }
            var queueUri = new Uri(endpoint, "api/contributions/publication-queue?limit=10");
            using (var missingQueueAuth = await client.GetAsync(queueUri))
            {
                await AssertErrorAsync(
                    missingQueueAuth, HttpStatusCode.Unauthorized, "authentication_required");
            }
            using (var contributorQueue = await SendAuthorizedAsync(
                       client, HttpMethod.Get, queueUri, contributorToken))
            {
                await AssertErrorAsync(contributorQueue, HttpStatusCode.Forbidden, "role_forbidden");
            }
            Assert.Empty(await GetJsonAsync<ContributionPublicationQueueItem[]>(
                client, queueUri, reviewerToken));
            var ccbQueueUri = new Uri(endpoint, "api/contributions/ccb-review-queue?limit=10");
            using (var contributorCcbQueue = await SendAuthorizedAsync(
                       client, HttpMethod.Get, ccbQueueUri, contributorToken))
            {
                await AssertErrorAsync(contributorCcbQueue, HttpStatusCode.Forbidden, "role_forbidden");
            }
            Assert.Empty(await GetJsonAsync<CcbReviewQueueItem[]>(
                client, ccbQueueUri, reviewerToken));
            var missingRetryUri = new Uri(
                endpoint,
                $"api/contributions/publication-queue/contribution-approval:{new string('a', 64)}/retry");
            using (var contributorRetry = await SendAuthorizedAsync(
                       client, HttpMethod.Post, missingRetryUri, contributorToken))
            {
                await AssertErrorAsync(contributorRetry, HttpStatusCode.Forbidden, "role_forbidden");
            }
            using (var missingRetry = await SendAuthorizedAsync(
                       client, HttpMethod.Post, missingRetryUri, reviewerToken))
            {
                await AssertErrorAsync(
                    missingRetry, HttpStatusCode.NotFound, "publication_dead_letter_missing");
            }
            var missingCcbRetryUri = new Uri(
                endpoint,
                $"api/contributions/ccb-review-queue/ccb-review:{new string('a', 64)}/retry");
            using (var missingCcbRetry = await SendAuthorizedAsync(
                       client, HttpMethod.Post, missingCcbRetryUri, reviewerToken))
            {
                await AssertErrorAsync(
                    missingCcbRetry, HttpStatusCode.NotFound, "ccb_review_dead_letter_missing");
            }
            var now = DateTimeOffset.UtcNow;
            var valid = Prepare(Candidate(
                "candidate:api-approved", "resource:api-approved", privateBody), now, "nonce-api-approved-1234");
            var intakeUri = new Uri(endpoint, "api/contributions/intake");

            using (var missingAuth = await SendIntakeAsync(client, intakeUri, valid, token: null))
            {
                await AssertErrorAsync(missingAuth, HttpStatusCode.Unauthorized, "authentication_required");
            }
            using (var invalidAuth = await SendIntakeAsync(client, intakeUri, valid, wrongToken))
            {
                await AssertErrorAsync(invalidAuth, HttpStatusCode.Unauthorized, "authentication_invalid");
            }
            using (var wrongRole = await SendIntakeAsync(client, intakeUri, valid, reviewerToken))
            {
                await AssertErrorAsync(wrongRole, HttpStatusCode.Forbidden, "role_forbidden");
            }
            var wrongIdentityCandidate = valid.Candidate with
            {
                CandidateId = "candidate:api-wrong-identity",
                ResourceId = "resource:api-wrong-identity",
                ContributorPseudonym = "contributor:other",
            };
            var wrongIdentity = Prepare(
                wrongIdentityCandidate, now, "nonce-api-wrong-identity");
            using (var mismatchedIdentity = await SendIntakeAsync(
                       client, intakeUri, wrongIdentity, contributorToken))
            {
                await AssertErrorAsync(
                    mismatchedIdentity,
                    HttpStatusCode.Forbidden,
                    "contributor_identity_mismatch");
            }
            using (var changedAfterApproval = await SendIntakeAsync(
                       client,
                       intakeUri,
                       valid with
                       {
                           Candidate = valid.Candidate with { Body = privateBody + " Changed after approval." },
                       },
                       contributorToken))
            {
                await AssertErrorAsync(
                    changedAfterApproval,
                    HttpStatusCode.Conflict,
                    "item_approval_invalid_or_expired");
            }
            using (var accepted = await SendIntakeAsync(client, intakeUri, valid, contributorToken))
            {
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            }
            using (var duplicate = await SendIntakeAsync(client, intakeUri, valid, contributorToken))
            {
                Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            }

            var pending = await GetJsonAsync<ContributionReviewItem[]>(
                client, new Uri(endpoint, "api/contributions/pending?limit=10"), reviewerToken);
            Assert.Equal(valid.SubmissionId, Assert.Single(pending).SubmissionId);
            var status = await GetJsonAsync<ContributionReviewItem>(
                client,
                new Uri(endpoint, $"api/contributions/{Uri.EscapeDataString(valid.SubmissionId)}"),
                contributorToken);
            Assert.Equal(ContributionState.Quarantined, status.State);
            Assert.Equal(privateBody, status.Candidate.Body);

            var approveUri = new Uri(
                endpoint,
                $"api/contributions/{Uri.EscapeDataString(valid.SubmissionId)}/approve");
            using (var forbiddenApproval = await SendAuthorizedAsync(
                       client, HttpMethod.Post, approveUri, contributorToken))
            {
                await AssertErrorAsync(forbiddenApproval, HttpStatusCode.Forbidden, "role_forbidden");
            }
            GlobalReleaseDescriptor approvedRelease;
            using (var approved = await SendAuthorizedAsync(
                       client, HttpMethod.Post, approveUri, reviewerToken))
            {
                Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                approvedRelease = (await approved.Content.ReadFromJsonAsync<GlobalReleaseDescriptor>())!;
            }
            using (var replayedApproval = await SendAuthorizedAsync(
                       client, HttpMethod.Post, approveUri, reviewerToken))
            {
                await AssertErrorAsync(
                    replayedApproval, HttpStatusCode.Conflict, "submission_not_quarantined");
            }

            var ccbArtifact = CcbContributionPackageTests.CreatePackage();
            var ccbSha256 = Convert.ToHexStringLower(SHA256.HashData(ccbArtifact));
            var ccbCandidate = new ContributionCandidate(
                "candidate:api-ccb",
                "tool:community-upper",
                "1.0.0",
                KnowledgeResourceKind.Ccb,
                "Community uppercase tool",
                "A sandboxed uppercase transformation with two deterministic verification fixtures.",
                "https://example.test/community-upper",
                "MIT",
                ["text.upper"],
                [],
                "contributor:api-fixture",
                ccbSha256);
            var ccb = Prepare(ccbCandidate, now, "nonce-api-ccb-123456789");
            using (var ccbAccepted = await SendIntakeAsync(
                       client, intakeUri, ccb, contributorToken, ccbArtifact))
            {
                Assert.Equal(HttpStatusCode.Accepted, ccbAccepted.StatusCode);
            }
            var ccbApproveUri = new Uri(
                endpoint,
                $"api/contributions/{Uri.EscapeDataString(ccb.SubmissionId)}/approve");
            using (var ccbApproval = await SendAuthorizedAsync(
                       client, HttpMethod.Post, ccbApproveUri, reviewerToken))
            {
                await AssertErrorAsync(
                    ccbApproval, HttpStatusCode.Conflict, "ccb_sandbox_review_required");
            }

            var transport = new HttpGlobalRegistryTransport(client, endpoint);
            var replica = await GlobalReplicaClient.OpenAsync(
                Path.Combine(root, "client"), key.ExportSubjectPublicKeyInfo());
            await replica.SynchronizeAsync(transport);
            Assert.Equal(approvedRelease.Sequence, (await replica.GetStatusAsync()).Sequence);
            Assert.Equal(
                "resource:api-approved",
                Assert.Single(await replica.SearchAsync("authenticated loopback", null)).ResourceId);

            var risky = Prepare(Candidate(
                "candidate:api-risky", "resource:api-risky", riskyBody), now, "nonce-api-risky-1234567");
            using (var riskyAccepted = await SendIntakeAsync(client, intakeUri, risky, contributorToken))
            {
                Assert.Equal(HttpStatusCode.Accepted, riskyAccepted.StatusCode);
            }
            var riskyApproveUri = new Uri(
                endpoint,
                $"api/contributions/{Uri.EscapeDataString(risky.SubmissionId)}/approve");
            using (var riskyApproval = await SendAuthorizedAsync(
                       client, HttpMethod.Post, riskyApproveUri, reviewerToken))
            {
                await AssertErrorAsync(
                    riskyApproval, HttpStatusCode.Conflict, "quarantine_findings_present");
            }

            var withdrawUri = new Uri(
                endpoint,
                $"api/contributions/{Uri.EscapeDataString(valid.SubmissionId)}/withdraw");
            using (var forbiddenWithdrawal = await SendAuthorizedAsync(
                       client, HttpMethod.Post, withdrawUri, reviewerToken))
            {
                await AssertErrorAsync(forbiddenWithdrawal, HttpStatusCode.Forbidden, "role_forbidden");
            }
            using (var withdrawn = await SendAuthorizedAsync(
                       client, HttpMethod.Post, withdrawUri, contributorToken))
            {
                Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
            }
            await replica.SynchronizeAsync(transport);
            Assert.True(Assert.Single(await replica.SearchAsync("authenticated loopback", null)).Deprecated);
            Assert.Equal(2, (await replica.GetStatusAsync()).Sequence);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            var diagnostics = await outputTask + await errorTask;
            Assert.DoesNotContain(contributorToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(reviewerToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(privateBody, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(riskyBody, diagnostics, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LoopbackApi_RateLimitsUnauthenticatedRequestsWithoutLoggingCredentials()
    {
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "rate-limit-key.pk8");
        await File.WriteAllBytesAsync(keyPath, key.ExportPkcs8PrivateKey());
        var contributorToken = $"contributor-{Guid.NewGuid():N}";
        var reviewerToken = $"reviewer-{Guid.NewGuid():N}";
        var endpoint = new Uri($"http://127.0.0.1:{ReserveEphemeralPort()}/");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(GlobalRegistryRepository).Assembly.Location);
        startInfo.Environment["DCX_GLOBAL_SIMULATION"] = "1";
        startInfo.Environment["DCX_GLOBAL_DATA_ROOT"] = Path.Combine(root, "rate-limit-server");
        startInfo.Environment["DCX_GLOBAL_SIGNING_KEY"] = keyPath;
        startInfo.Environment["DCX_GLOBAL_SIMULATION_URL"] = endpoint.AbsoluteUri;
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_API"] = "1";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_REQUESTS_PER_MINUTE"] = "10";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTOR_TOKEN_SHA256"] = Digest(contributorToken);
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTOR_PSEUDONYM"] = "contributor:rate-limit-fixture";
        startInfo.Environment["DCX_GLOBAL_REVIEWER_TOKEN_SHA256"] = Digest(reviewerToken);
        startInfo.Environment["DCX_GLOBAL_REVIEWER_ID"] = "reviewer:rate-limit-fixture";
        startInfo.Environment["Logging__LogLevel__Default"] = "Warning";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the rate-limit fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            await WaitForHealthAsync(client, endpoint, process, errorTask);
            var pendingUri = new Uri(endpoint, "api/contributions/pending?limit=1");
            for (var requestNumber = 0; requestNumber < 10; requestNumber++)
            {
                using var response = await client.GetAsync(pendingUri);
                await AssertErrorAsync(
                    response, HttpStatusCode.Unauthorized, "authentication_required");
            }

            using var limited = await client.GetAsync(pendingUri);
            await AssertErrorAsync(limited, HttpStatusCode.TooManyRequests, "rate_limited");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            var diagnostics = await outputTask + await errorTask;
            Assert.DoesNotContain(contributorToken, diagnostics, StringComparison.Ordinal);
            Assert.DoesNotContain(reviewerToken, diagnostics, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicationWorker_FailsStartupWhenContributionApiIsDisabled()
    {
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "worker-gate-key.pk8");
        await File.WriteAllBytesAsync(keyPath, key.ExportPkcs8PrivateKey());
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(GlobalRegistryRepository).Assembly.Location);
        startInfo.Environment["DCX_GLOBAL_SIMULATION"] = "1";
        startInfo.Environment["DCX_GLOBAL_DATA_ROOT"] = Path.Combine(root, "worker-gate-server");
        startInfo.Environment["DCX_GLOBAL_SIGNING_KEY"] = keyPath;
        startInfo.Environment["DCX_GLOBAL_SIMULATION_URL"] =
            $"http://127.0.0.1:{ReserveEphemeralPort()}/";
        startInfo.Environment["DCX_GLOBAL_CONTRIBUTION_WORKER"] = "1";
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the worker-gate fixture.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var diagnostics = (await outputTask) + (await errorTask);

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "contribution worker cannot run while the contribution API is disabled",
                diagnostics,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private static ContributionCandidate Candidate(string candidateId, string resourceId, string body) => new(
        candidateId,
        resourceId,
        "1",
        KnowledgeResourceKind.Ckb,
        "Authenticated contribution fixture",
        body,
        "https://example.test/api-contribution",
        "CC-BY-4.0",
        [],
        [],
        "contributor:api-fixture");

    private static PreparedContribution Prepare(
        ContributionCandidate candidate,
        DateTimeOffset now,
        string nonce) =>
        new ContributionPreparationService(new ContributionScanner()).Prepare(
            new ContributionPreferences(true),
            candidate,
            new ContributionAttestation(true, true, true),
            new ContributionItemApproval(
                candidate.CandidateId,
                ContributionCodec.CandidateSha256(candidate),
                nonce,
                now),
            now);

    private static async Task<HttpResponseMessage> SendIntakeAsync(
        HttpClient client,
        Uri uri,
        PreparedContribution contribution,
        string? token,
        byte[]? artifact = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new ByteArrayContent(ContributionTransportCodec.Encode(
                contribution,
                artifact ?? [])),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContributionTransportCodec.MediaType);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, Uri uri, string token)
    {
        using var response = await SendAuthorizedAsync(client, HttpMethod.Get, uri, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task AssertErrorAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHealthAsync(
        HttpClient client,
        Uri endpoint,
        Process process,
        Task<string> errorTask)
    {
        var health = new Uri(endpoint, "health");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The contribution API fixture exited early: {await errorTask}");
            }

            try
            {
                using var response = await client.GetAsync(health);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The contribution API fixture did not become healthy.");
    }
}
