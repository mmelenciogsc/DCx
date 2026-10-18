using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelDependencyHttpTransferTests
{
    [Fact]
    public void ProductionHandler_DisablesRedirectsCookiesProxyAndDecompression()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(
            LocalModelDependencyHttpTransfer.CreateHardenedHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromSeconds(15), handler.ConnectTimeout);
    }

    [Fact]
    public async Task ExactApprovedItem_StreamsAndReturnsDigestBoundEvidence()
    {
        var bytes = "approved dependency fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new MemoryStream();
        var progress = new List<LocalModelDependencyItemTransferProgress>();

        var result = await transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            destination,
            progress: new InlineProgress<LocalModelDependencyItemTransferProgress>(progress.Add));

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(plan.PlanSha256, result.PlanSha256);
        Assert.Equal(item.SourceUri, result.SourceUri);
        Assert.Equal(item.ExpectedBytes, result.Bytes);
        Assert.Equal(item.Sha256, result.Sha256);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("identity", Assert.Single(handler.AcceptEncodings));
        Assert.True(handler.NoStore);
        var completed = Assert.Single(progress);
        Assert.Equal(bytes.LongLength, completed.BytesTransferred);
        Assert.Equal(bytes.LongLength, completed.ExpectedBytes);
    }

    [Fact]
    public async Task MissingApprovalOrCapacity_DeniesBeforeNetworkAndLeavesDestinationEmpty()
    {
        var bytes = "approval fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);

        await AssertCodeAsync("dependency_transfer_approval_invalid", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: false),
            new MemoryStream()));
        await AssertCodeAsync("dependency_transfer_capacity_unapproved", () => transfer.TransferAsync(
            plan with { ObservedAvailableFreeBytes = null, CapacitySufficient = false },
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            new MemoryStream()));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task MutatedPlanApprovalAndNonemptyDestination_FailBeforeNetwork()
    {
        var bytes = "binding fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        var approval = LocalModelDependencyTransferApproval.Create(plan, item, approved: true);

        await AssertCodeAsync("dependency_transfer_failed", () => transfer.TransferAsync(
            plan with { TransferBytes = plan.TransferBytes + 1 }, approval, new MemoryStream()));
        await AssertCodeAsync("dependency_transfer_approval_invalid", () => transfer.TransferAsync(
            plan, approval with { Sha256 = new string('0', 64) }, new MemoryStream()));
        await AssertCodeAsync("dependency_transfer_destination_invalid", () => transfer.TransferAsync(
            plan, approval, new MemoryStream([1])));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RedirectFinalUriEncodingRangeAndChunkedResponsesFailClosed()
    {
        var bytes = "response policy fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var approval = LocalModelDependencyTransferApproval.Create(plan, item, approved: true);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);

        handler.Respond = request => Response(
            request,
            bytes,
            status: HttpStatusCode.Redirect,
            finalUri: new Uri("https://github.com/redirected"));
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_response_invalid");

        handler.Respond = request =>
        {
            var response = Response(request, bytes);
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        };
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_response_invalid");

        handler.Respond = request =>
        {
            var response = Response(request, bytes);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
            return response;
        };
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_response_invalid");

        handler.Respond = request =>
        {
            var response = Response(request, bytes);
            response.Headers.TransferEncodingChunked = true;
            return response;
        };
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_response_invalid");
    }

    [Fact]
    public async Task MissingWrongShortAndExtraLengthsRemovePartialOutput()
    {
        var bytes = "length fixture payload"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var approval = LocalModelDependencyTransferApproval.Create(plan, item, approved: true);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);

        handler.Respond = request =>
        {
            var response = Response(request, bytes);
            response.Content.Headers.ContentLength = null;
            return response;
        };
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_response_invalid");

        handler.Respond = request => Response(request, bytes[..^1], declaredLength: bytes.Length);
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_length_invalid");

        var extra = bytes.Concat([(byte)42]).ToArray();
        handler.Respond = request => Response(request, extra, declaredLength: bytes.Length);
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_length_invalid");
    }

    [Fact]
    public async Task DigestMismatchAndNetworkFailureRemovePartialOutputWithoutLeakingDetails()
    {
        var bytes = "digest fixture payload"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var approval = LocalModelDependencyTransferApproval.Create(plan, item, approved: true);
        var handler = new FixtureHandler(request => Response(request, bytes.ToArray()));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);

        var changed = bytes.ToArray();
        changed[0] ^= 0xff;
        handler.Respond = request => Response(request, changed);
        await DeniedAndEmptyAsync(transfer, plan, approval, "dependency_transfer_failed");

        handler.Respond = _ => throw new HttpRequestException("synthetic private network detail");
        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(() =>
            transfer.TransferAsync(plan, approval, new MemoryStream()));
        Assert.Equal("dependency_transfer_failed", exception.Code);
        Assert.Equal("dependency_transfer_failed", exception.Message);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationAfterPartialReadTruncatesDestination()
    {
        var bytes = Enumerable.Repeat((byte)0x5a, 96 * 1024).ToArray();
        var (catalog, plan, item) = Plan(bytes);
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler(request =>
        {
            var content = new StreamContent(new CancelAfterFirstReadStream(bytes, cancellation));
            content.Headers.ContentLength = bytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            };
        });
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new MemoryStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            destination,
            cancellation.Token));
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    [Fact]
    public async Task ReadFailureAfterWrittenChunkTruncatesPartialDestination()
    {
        var bytes = Enumerable.Repeat((byte)0x4d, 96 * 1024).ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request =>
        {
            var content = new StreamContent(new ThrowAfterFirstReadStream(bytes));
            content.Headers.ContentLength = bytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content,
            };
        });
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new MemoryStream();

        await AssertCodeAsync("dependency_transfer_failed", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            destination));
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    [Fact]
    public async Task InternalDeadlineReturnsStableTimeoutAndEmptyDestination()
    {
        var bytes = "timeout fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        using var transfer = new LocalModelDependencyHttpTransfer(
            new NeverCompletesHandler(),
            catalog,
            TimeSpan.FromMilliseconds(25));
        using var destination = new MemoryStream();

        await AssertCodeAsync("dependency_transfer_timeout", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            destination));
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    private static async Task DeniedAndEmptyAsync(
        LocalModelDependencyHttpTransfer transfer,
        LocalModelDependencyTransferPlan plan,
        LocalModelDependencyTransferApproval approval,
        string code)
    {
        using var destination = new MemoryStream();
        await AssertCodeAsync(code, () => transfer.TransferAsync(plan, approval, destination));
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
    }

    [Fact]
    public async Task SelfConsistentPlanFromDifferentCatalogIsDeniedBeforeNetwork()
    {
        var bytes = "catalog binding fixture"u8.ToArray();
        var (trustedCatalog, _, _) = Plan("trusted fixture"u8.ToArray());
        var (_, untrustedPlan, untrustedItem) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, trustedCatalog);

        await AssertCodeAsync("dependency_transfer_catalog_invalid", () => transfer.TransferAsync(
            untrustedPlan,
            LocalModelDependencyTransferApproval.Create(untrustedPlan, untrustedItem, approved: true),
            new MemoryStream()));
        Assert.Equal(0, handler.Requests);
    }

    private static (
        LocalModelDependencyCatalogReview Catalog,
        LocalModelDependencyTransferPlan Plan,
        LocalModelDependencyTransferItem Item) Plan(byte[] bytes)
    {
        var item = new LocalModelDependencyTransferItem(
            new Uri("https://github.com/dcx/dependency.bin"),
            bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Array.AsReadOnly(["candidate/test-dependency"]));
        var catalog = new LocalModelDependencyCatalogReview(
            LocalModelDependencyCatalogLoader.CurrentSchemaVersion,
            "fixture-catalog",
            new string('a', 64),
            Array.AsReadOnly([
                new PinnedLocalModelDependencyCandidate(
                    "test-dependency",
                    LocalModelDependencyKind.WhisperModel,
                    "Test dependency",
                    item.SourceUri,
                    item.ExpectedBytes,
                    item.Sha256,
                    new LocalModelDependencyLicense(
                        "MIT",
                        new Uri("https://raw.githubusercontent.com/dcx/license/LICENSE"),
                        1,
                        Convert.ToHexStringLower(SHA256.HashData([42])),
                        false),
                    Array.Empty<OllamaManagedLayer>())
            ]));
        var plan = new LocalModelDependencyTransferPlanner().Create(
            catalog,
            ["test-dependency"],
            long.MaxValue);
        return (catalog, plan, Assert.Single(plan.Items, candidate => candidate.SourceUri == item.SourceUri));
    }

    private static HttpResponseMessage Response(
        HttpRequestMessage request,
        byte[] bytes,
        HttpStatusCode status = HttpStatusCode.OK,
        Uri? finalUri = null,
        long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentLength = declaredLength ?? bytes.LongLength;
        return new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(request.Method, finalUri ?? request.RequestUri),
            Content = content,
        };
    }

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        public int Requests { get; private set; }
        public List<string> AcceptEncodings { get; } = [];
        public bool NoStore { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            AcceptEncodings.AddRange(request.Headers.AcceptEncoding.Select(value => value.Value));
            NoStore = request.Headers.CacheControl?.NoStore == true;
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class CancelAfterFirstReadStream(
        byte[] bytes,
        CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        private bool first = true;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (first && read > 0)
            {
                first = false;
                cancellation.Cancel();
            }
            return read;
        }
    }

    private sealed class ThrowAfterFirstReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool first = true;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!first) throw new IOException("synthetic private stream failure");
            first = false;
            return base.ReadAsync(buffer, cancellationToken);
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
