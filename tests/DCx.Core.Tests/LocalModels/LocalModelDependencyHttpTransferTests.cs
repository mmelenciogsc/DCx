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
        Assert.Null(handler.Credentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(16, handler.MaxResponseHeadersLength);
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
        Assert.Equal(transfer.RedirectPolicyReview.PolicySha256, result.RedirectPolicySha256);
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
    public async Task DefaultPolicy_DeniesRedirectBeforeFollowingAndRollsBack()
    {
        var bytes = "redirect denied fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var target = new Uri("https://assets.example.test/releases/dependency.bin?signature=fixture");
        var handler = new FixtureHandler(request => Redirect(request, target));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new MemoryStream();

        await AssertCodeAsync("dependency_transfer_redirect_denied", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            destination));

        Assert.Equal(0, destination.Length);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(item.SourceUri, Assert.Single(handler.RequestedUris));
    }

    [Fact]
    public async Task ExactPolicy_FollowsApprovedRedirectWithoutForwardingAmbientHeaders()
    {
        var bytes = "redirect approved fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var target = new Uri("https://assets.example.test/releases/dependency.bin?signature=fixture");
        var handler = new FixtureHandler(request =>
            request.RequestUri == item.SourceUri
                ? Redirect(request, target)
                : Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(
            handler,
            catalog,
            redirectPolicy: ExactPolicy(
                item,
                (new Uri("https://github.com/"), new Uri("https://assets.example.test/releases/"))));
        using var destination = new MemoryStream();

        var result = await transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(
                plan,
                item,
                transfer.RedirectPolicyReview,
                approved: true),
            destination);

        Assert.Equal(bytes, destination.ToArray());
        Assert.Equal(item.SourceUri, result.SourceUri);
        Assert.Equal(transfer.RedirectPolicyReview.PolicySha256, result.RedirectPolicySha256);
        Assert.Equal([item.SourceUri, target], handler.RequestedUris);
        Assert.All(handler.AcceptEncodings, value => Assert.Equal("identity", value));
        Assert.All(handler.NoStoreValues, Assert.True);
        Assert.All(handler.AuthorizationPresent, Assert.False);
        Assert.All(handler.CookiePresent, Assert.False);
        Assert.All(handler.ReferrerPresent, Assert.False);
    }

    [Fact]
    public async Task UnsafeRedirectTargetsFailBeforeSecondRequest()
    {
        var bytes = "unsafe redirect fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var targets = new Uri[]
        {
            new("/relative", UriKind.Relative),
            new("http://assets.example.test/releases/dependency.bin"),
            new("https://127.0.0.1/releases/dependency.bin"),
            new("https://user@assets.example.test/releases/dependency.bin"),
            new("https://assets.example.test:444/releases/dependency.bin"),
            new("https://assets.example.test/releases/dependency.bin#fragment"),
            new($"https://assets.example.test/releases/dependency.bin?{new string('a', 2049)}"),
        };

        foreach (var target in targets)
        {
            var handler = new FixtureHandler(request => Redirect(request, target));
            using var transfer = new LocalModelDependencyHttpTransfer(
                handler,
                catalog,
                redirectPolicy: new ReviewOnlyRedirectPolicy());
            using var destination = new MemoryStream();

            await AssertCodeAsync("dependency_transfer_redirect_invalid", () => transfer.TransferAsync(
                plan,
                LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
                destination));
            Assert.Equal(0, destination.Length);
            Assert.Equal(1, handler.Requests);
        }
    }

    [Fact]
    public async Task RedirectLoopAndHopLimitFailClosed()
    {
        var bytes = "redirect bound fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var first = new Uri("https://one.example.test/files/one.bin");
        var second = new Uri("https://two.example.test/files/two.bin");
        var third = new Uri("https://three.example.test/files/three.bin");
        var fourth = new Uri("https://four.example.test/files/four.bin");
        var policy = ExactPolicy(
            item,
            (new Uri("https://github.com/"), new Uri("https://one.example.test/files/")),
            (new Uri("https://one.example.test/"), new Uri("https://two.example.test/files/")),
            (new Uri("https://two.example.test/"), new Uri("https://three.example.test/files/")),
            (new Uri("https://three.example.test/"), new Uri("https://four.example.test/files/")),
            (new Uri("https://one.example.test/"), new Uri("https://github.com/dcx/")));

        var loopHandler = new FixtureHandler(request =>
            request.RequestUri == item.SourceUri
                ? Redirect(request, first)
                : Redirect(request, item.SourceUri));
        using (var loopTransfer = new LocalModelDependencyHttpTransfer(
            loopHandler,
            catalog,
            redirectPolicy: policy))
        {
            await DeniedAndEmptyAsync(
                loopTransfer,
                plan,
                LocalModelDependencyTransferApproval.Create(
                    plan,
                    item,
                    loopTransfer.RedirectPolicyReview,
                    approved: true),
                "dependency_transfer_redirect_invalid");
            Assert.Equal(2, loopHandler.Requests);
        }

        var hopHandler = new FixtureHandler(request => Redirect(
            request,
            request.RequestUri == item.SourceUri ? first :
            request.RequestUri == first ? second :
            request.RequestUri == second ? third : fourth));
        using var hopTransfer = new LocalModelDependencyHttpTransfer(
            hopHandler,
            catalog,
            redirectPolicy: policy);
        await DeniedAndEmptyAsync(
            hopTransfer,
            plan,
            LocalModelDependencyTransferApproval.Create(
                plan,
                item,
                hopTransfer.RedirectPolicyReview,
                approved: true),
            "dependency_transfer_redirect_limit");
        Assert.Equal(4, hopHandler.Requests);
    }

    [Fact]
    public void ExactPolicy_DeniesChangedItemTransitionAndBroadOrDuplicateRules()
    {
        var bytes = "redirect policy fixture"u8.ToArray();
        var (_, _, item) = Plan(bytes);
        var from = new Uri("https://github.com/");
        var prefix = new Uri("https://assets.example.test/releases/");
        var policy = ExactPolicy(item, (from, prefix));
        var target = new Uri("https://assets.example.test/releases/dependency.bin?signature=fixture");

        Assert.True(policy.IsAllowed(item, item.SourceUri, target, 1));
        Assert.False(policy.IsAllowed(item with { ExpectedBytes = item.ExpectedBytes + 1 }, item.SourceUri, target, 1));
        Assert.False(policy.IsAllowed(item, item.SourceUri, new Uri("https://assets.example.test/other/dependency.bin"), 1));
        Assert.False(policy.IsAllowed(item, item.SourceUri, target, 0));
        Assert.Throws<ArgumentException>(() => ExactPolicy(
            item,
            (from, new Uri("https://assets.example.test"))));
        Assert.Throws<ArgumentException>(() => ExactPolicy(item, (from, prefix), (from, prefix)));
    }

    [Fact]
    public void RedirectPolicyReview_IsCanonicalAndRejectsMalformedReview()
    {
        var (_, _, item) = Plan("canonical redirect policy fixture"u8.ToArray());
        var first = (new Uri("https://github.com/"), new Uri("https://one.example.test/files/"));
        var second = (new Uri("https://one.example.test/"), new Uri("https://two.example.test/files/"));
        var forward = ExactPolicy(item, first, second).Review;
        var reverse = ExactPolicy(item, second, first).Review;

        Assert.Equal(forward.PolicySha256, reverse.PolicySha256);
        Assert.Equal(forward.Routes, reverse.Routes);
        Assert.True(forward.AllowsRedirects);
        Assert.NotEqual(LocalModelDependencyRedirectPolicyReview.DenyAll.PolicySha256, forward.PolicySha256);
        Assert.Throws<InvalidOperationException>(() =>
            LocalModelDependencyRedirectPolicyReview.Validate(
                forward with { PolicySha256 = new string('0', 64) }));
        Assert.Throws<InvalidOperationException>(() =>
            LocalModelDependencyRedirectPolicyReview.Validate(
                forward with { Routes = Array.AsReadOnly(forward.Routes.Reverse().ToArray()) }));
    }

    [Fact]
    public async Task RedirectPolicyApprovalSubstitutionFailsBeforeNetwork()
    {
        var bytes = "redirect approval binding fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var policy = ExactPolicy(
            item,
            (new Uri("https://github.com/"), new Uri("https://assets.example.test/releases/")));
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog, redirectPolicy: policy);

        await AssertCodeAsync("dependency_transfer_approval_invalid", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            new MemoryStream()));

        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task RedirectPolicyMutationDuringTransferRollsBackAuthenticatedBytes()
    {
        var bytes = "redirect mutation fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var mutablePolicy = new MutableRedirectPolicy(ExactPolicy(
            item,
            (new Uri("https://github.com/"), new Uri("https://assets.example.test/releases/"))).Review);
        var handler = new FixtureHandler(request =>
        {
            mutablePolicy.Review = LocalModelDependencyRedirectPolicyReview.DenyAll;
            return Response(request, bytes);
        });
        using var transfer = new LocalModelDependencyHttpTransfer(
            handler,
            catalog,
            redirectPolicy: mutablePolicy);
        using var destination = new MemoryStream();
        var approval = LocalModelDependencyTransferApproval.Create(
            plan,
            item,
            transfer.RedirectPolicyReview,
            approved: true);

        await AssertCodeAsync("dependency_transfer_approval_invalid", () =>
            transfer.TransferAsync(plan, approval, destination));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
    }

    [Fact]
    public async Task InjectedPolicyCannotOverrideReviewedDenyAllAuthority()
    {
        var bytes = "review authority fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var target = new Uri("https://assets.example.test/releases/dependency.bin");
        var handler = new FixtureHandler(request => Redirect(request, target));
        using var transfer = new LocalModelDependencyHttpTransfer(
            handler,
            catalog,
            redirectPolicy: new ReviewOnlyRedirectPolicy());

        await AssertCodeAsync("dependency_transfer_redirect_denied", () => transfer.TransferAsync(
            plan,
            LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
            new MemoryStream()));

        Assert.Equal(1, handler.Requests);
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
        using var nonempty = new MemoryStream([1]);
        await AssertCodeAsync("dependency_transfer_destination_invalid", () => transfer.TransferAsync(
            plan, approval, nonempty));
        Assert.Equal([1], nonempty.ToArray());
        Assert.Equal(0, nonempty.Position);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task SubstitutedFinalUriEncodingRangeAndChunkedResponsesFailClosed()
    {
        var bytes = "response policy fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var approval = LocalModelDependencyTransferApproval.Create(plan, item, approved: true);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);

        handler.Respond = request => Response(
            request,
            bytes,
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
    public async Task UnexpectedProgressFailureRollsBackWithoutLeakingDetails()
    {
        var bytes = "unexpected progress failure fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new MemoryStream();

        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(() =>
            transfer.TransferAsync(
                plan,
                LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
                destination,
                progress: new InlineProgress<LocalModelDependencyItemTransferProgress>(_ =>
                    throw new ApplicationException("synthetic private callback detail"))));

        Assert.Equal("dependency_transfer_failed", exception.Code);
        Assert.Equal("dependency_transfer_failed", exception.Message);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, destination.Length);
        Assert.Equal(0, destination.Position);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task UnexpectedRollbackFailureReturnsStableCleanupCode()
    {
        var bytes = "unexpected cleanup failure fixture"u8.ToArray();
        var (catalog, plan, item) = Plan(bytes);
        var handler = new FixtureHandler(request => Response(request, bytes));
        using var transfer = new LocalModelDependencyHttpTransfer(handler, catalog);
        using var destination = new RollbackFailureStream();

        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(() =>
            transfer.TransferAsync(
                plan,
                LocalModelDependencyTransferApproval.Create(plan, item, approved: true),
                destination,
                progress: new InlineProgress<LocalModelDependencyItemTransferProgress>(_ =>
                    throw new ApplicationException("synthetic private callback detail"))));

        Assert.Equal("dependency_transfer_cleanup_failed", exception.Code);
        Assert.Equal("dependency_transfer_cleanup_failed", exception.Message);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes.LongLength, destination.Length);
        Assert.Equal(1, handler.Requests);
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

    private static HttpResponseMessage Redirect(
        HttpRequestMessage request,
        Uri target,
        HttpStatusCode status = HttpStatusCode.Redirect)
    {
        var response = new HttpResponseMessage(status)
        {
            RequestMessage = new HttpRequestMessage(request.Method, request.RequestUri),
            Content = new ByteArrayContent([]),
        };
        response.Headers.Location = target;
        return response;
    }

    private static ExactLocalModelDependencyRedirectPolicy ExactPolicy(
        LocalModelDependencyTransferItem item,
        params (Uri FromOrigin, Uri TargetPrefix)[] transitions) =>
        new(Array.AsReadOnly(transitions.Select(transition =>
            new LocalModelDependencyRedirectRule(
                item.SourceUri,
                item.ExpectedBytes,
                item.Sha256,
                transition.FromOrigin,
                transition.TargetPrefix)).ToArray()));

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;
        public int Requests { get; private set; }
        public List<Uri> RequestedUris { get; } = [];
        public List<string> AcceptEncodings { get; } = [];
        public List<bool> NoStoreValues { get; } = [];
        public List<bool> AuthorizationPresent { get; } = [];
        public List<bool> CookiePresent { get; } = [];
        public List<bool> ReferrerPresent { get; } = [];
        public bool NoStore { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            RequestedUris.Add(request.RequestUri!);
            AcceptEncodings.AddRange(request.Headers.AcceptEncoding.Select(value => value.Value));
            NoStore = request.Headers.CacheControl?.NoStore == true;
            NoStoreValues.Add(NoStore);
            AuthorizationPresent.Add(request.Headers.Authorization is not null);
            CookiePresent.Add(request.Headers.Contains("Cookie"));
            ReferrerPresent.Add(request.Headers.Referrer is not null);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class ReviewOnlyRedirectPolicy : ILocalModelDependencyRedirectPolicy
    {
        public LocalModelDependencyRedirectPolicyReview Review =>
            LocalModelDependencyRedirectPolicyReview.DenyAll;
    }

    private sealed class MutableRedirectPolicy(LocalModelDependencyRedirectPolicyReview review)
        : ILocalModelDependencyRedirectPolicy
    {
        public LocalModelDependencyRedirectPolicyReview Review { get; set; } = review;
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

    private sealed class RollbackFailureStream : MemoryStream
    {
        public override void SetLength(long value)
        {
            if (value == 0 && Length != 0)
            {
                throw new ApplicationException("synthetic private cleanup detail");
            }
            base.SetLength(value);
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
