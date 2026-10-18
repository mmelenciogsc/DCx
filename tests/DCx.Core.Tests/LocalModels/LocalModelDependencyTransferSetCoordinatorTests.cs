using System.Security.Cryptography;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelDependencyTransferSetCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "dcx-transfer-set-tests-" + Guid.NewGuid().ToString("N"));

    public LocalModelDependencyTransferSetCoordinatorTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Transfer_RequiresExplicitApprovalAndFreshCapacityBeforeCreatingStorage()
    {
        var fixture = Fixture("approval", freeBytes: 0);
        await AssertCodeAsync("dependency_transfer_approval_invalid", () => fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: false)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "LocalModels")));

        await AssertCodeAsync("dependency_transfer_capacity_unapproved", () => fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "LocalModels")));
        Assert.Equal(0, fixture.Transfer.Calls);
        Assert.Empty(fixture.Anchors.Values);
    }

    [Fact]
    public async Task Transfer_StreamsEveryItemProtectsReceiptAndAtomicallyPublishesVerifiableSet()
    {
        var fixture = Fixture("success");
        var receipt = await fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true));

        Assert.Equal(fixture.Plan.Items.Count, fixture.Transfer.Calls);
        Assert.Equal(fixture.Plan.PlanSha256, receipt.PlanSha256);
        Assert.Equal(fixture.Plan.TransferBytes, receipt.TransferBytes);
        Assert.Equal(fixture.Plan.Items.Count, receipt.Files.Count);
        Assert.All(receipt.Files, file =>
        {
            Assert.DoesNotContain("github", file.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw", file.FileName, StringComparison.OrdinalIgnoreCase);
        });

        var completed = CompletedRoot(fixture);
        Assert.True(Directory.Exists(completed));
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetDirectoryName(completed)!,
            ".incoming-*"));
        var anchor = Assert.Single(fixture.Anchors.Values);
        Assert.Equal(LocalModelDependencyTransferSetCoordinator.AnchorStateKind, anchor.StateKind);
        Assert.Equal(fixture.Plan.PlanSha256, anchor.StorageId);

        var verified = await fixture.Coordinator.VerifyAsync(fixture.Plan);
        Assert.Equal(receipt.SchemaVersion, verified.SchemaVersion);
        Assert.Equal(receipt.CatalogId, verified.CatalogId);
        Assert.Equal(receipt.CatalogSha256, verified.CatalogSha256);
        Assert.Equal(receipt.PlanSha256, verified.PlanSha256);
        Assert.Equal(receipt.TransferBytes, verified.TransferBytes);
        Assert.Equal(receipt.Files, verified.Files);
    }

    [Fact]
    public async Task TransferFailureAndProtectedWriteFailureLeaveNoPublishedOrPartialSet()
    {
        var transferFailure = Fixture("transfer-failure");
        transferFailure.Transfer.FailAtCall = 2;
        await Assert.ThrowsAsync<IOException>(() => transferFailure.Coordinator.TransferAsync(
            transferFailure.Plan,
            LocalModelDependencyTransferSetApproval.Create(transferFailure.Plan, approved: true)));
        AssertNoSetOrIncoming(transferFailure);
        Assert.Empty(transferFailure.Anchors.Values);

        var anchorFailure = Fixture("anchor-failure");
        anchorFailure.Anchors.FailWrites = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => anchorFailure.Coordinator.TransferAsync(
            anchorFailure.Plan,
            LocalModelDependencyTransferSetApproval.Create(anchorFailure.Plan, approved: true)));
        AssertNoSetOrIncoming(anchorFailure);

        var resultFailure = Fixture("result-failure");
        resultFailure.Transfer.ReturnWrongDigestAtCall = 1;
        await AssertCodeAsync("dependency_transfer_result_invalid", () => resultFailure.Coordinator.TransferAsync(
            resultFailure.Plan,
            LocalModelDependencyTransferSetApproval.Create(resultFailure.Plan, approved: true)));
        AssertNoSetOrIncoming(resultFailure);
        Assert.Empty(resultFailure.Anchors.Values);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringTransferLeavesNoPublishedOrPartialSet()
    {
        var before = Fixture("cancel-before");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.Coordinator.TransferAsync(
                before.Plan,
                LocalModelDependencyTransferSetApproval.Create(before.Plan, approved: true),
                canceled.Token));
        }
        Assert.False(Directory.Exists(Path.Combine(before.Root, "LocalModels")));

        var during = Fixture("cancel-during");
        using var cancellation = new CancellationTokenSource();
        during.Transfer.CancelAtCall = 2;
        during.Transfer.Cancellation = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => during.Coordinator.TransferAsync(
            during.Plan,
            LocalModelDependencyTransferSetApproval.Create(during.Plan, approved: true),
            cancellation.Token));
        AssertNoSetOrIncoming(during);
        Assert.Empty(during.Anchors.Values);
    }

    [Fact]
    public async Task Verify_DeniesMissingProtectionTamperedPayloadAndUnexpectedFile()
    {
        var fixture = Fixture("verify-denials");
        var receipt = await fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true));
        var anchor = Assert.Single(fixture.Anchors.Values);

        fixture.Anchors.RemoveAll();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.VerifyAsync(fixture.Plan));
        await fixture.Anchors.WriteAsync(anchor);

        var completed = CompletedRoot(fixture);
        var first = receipt.Files[0];
        var firstPath = Path.Combine(completed, first.FileName);
        var original = await File.ReadAllBytesAsync(firstPath);
        var changed = original.ToArray();
        changed[0] ^= 0xff;
        await File.WriteAllBytesAsync(firstPath, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.VerifyAsync(fixture.Plan));
        await File.WriteAllBytesAsync(firstPath, original);

        await File.WriteAllTextAsync(Path.Combine(completed, "unexpected.txt"), "unexpected");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.VerifyAsync(fixture.Plan));
    }

    [Fact]
    public async Task Verify_DeniesLinkedPayloadWithoutReadingOutsideContent()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = Fixture("linked-payload");
        var receipt = await fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true));
        var completed = CompletedRoot(fixture);
        var payload = Path.Combine(completed, receipt.Files[0].FileName);
        var outside = Path.Combine(root, "outside.bin");
        await File.WriteAllTextAsync(outside, "outside private sentinel");
        File.Delete(payload);
        File.CreateSymbolicLink(payload, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.VerifyAsync(fixture.Plan));
        Assert.Equal("outside private sentinel", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task Transfer_RejectsLinkedStorageRootWithoutWritingOutside()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixtureRoot = Path.Combine(root, "linked-root");
        Directory.CreateDirectory(fixtureRoot);
        var outside = Path.Combine(root, "outside-root");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(fixtureRoot, "LocalModels"), outside);
        var fixture = FixtureAt(fixtureRoot, "linked-root");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Empty(fixture.Anchors.Values);
    }

    [Fact]
    public async Task Cleanup_RemovesOnlyStrictIncomingNamesAndLeavesOtherDirectories()
    {
        var fixture = Fixture("cleanup");
        var transfers = Path.Combine(fixture.Root, "LocalModels", "dependency-transfers");
        Directory.CreateDirectory(transfers);
        var incoming = Path.Combine(transfers, ".incoming-" + new string('a', 32));
        var nearMatch = Path.Combine(transfers, ".incoming-not-a-guid");
        var retained = Path.Combine(transfers, "retained");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(nearMatch);
        Directory.CreateDirectory(retained);
        await File.WriteAllTextAsync(Path.Combine(incoming, "partial"), "partial");

        Assert.Equal(1, await fixture.Coordinator.CleanupAbandonedAsync());
        Assert.False(Directory.Exists(incoming));
        Assert.True(Directory.Exists(nearMatch));
        Assert.True(Directory.Exists(retained));

        if (OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(root, "cleanup-outside");
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "sentinel"), "outside");
            var linkedIncoming = Path.Combine(transfers, ".incoming-" + new string('b', 32));
            Directory.CreateSymbolicLink(linkedIncoming, outside);
            Assert.Equal(0, await fixture.Coordinator.CleanupAbandonedAsync());
            Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel")));
        }
    }

    [Fact]
    public async Task ExistingCompletedSetIsNeverOverwritten()
    {
        var fixture = Fixture("existing");
        await fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true));
        var receiptPath = Path.Combine(CompletedRoot(fixture), "receipt.json");
        var original = await File.ReadAllBytesAsync(receiptPath);

        await AssertCodeAsync("dependency_transfer_destination_exists", () => fixture.Coordinator.TransferAsync(
            fixture.Plan,
            LocalModelDependencyTransferSetApproval.Create(fixture.Plan, approved: true)));
        Assert.Equal(original, await File.ReadAllBytesAsync(receiptPath));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private FixtureState Fixture(string name, long freeBytes = long.MaxValue) =>
        FixtureAt(Path.Combine(root, name), name, freeBytes);

    private static FixtureState FixtureAt(
        string fixtureRoot,
        string name,
        long freeBytes = long.MaxValue)
    {
        Directory.CreateDirectory(fixtureRoot);
        var candidateBytes = System.Text.Encoding.UTF8.GetBytes("candidate-" + name);
        var licenseBytes = System.Text.Encoding.UTF8.GetBytes("license-" + name);
        var candidate = new PinnedLocalModelDependencyCandidate(
            "fixture-dependency",
            LocalModelDependencyKind.WhisperModel,
            "Fixture dependency",
            new Uri("https://github.com/dcx/fixture.bin"),
            candidateBytes.LongLength,
            Digest(candidateBytes),
            new LocalModelDependencyLicense(
                "MIT",
                new Uri("https://raw.githubusercontent.com/dcx/fixture/LICENSE"),
                licenseBytes.LongLength,
                Digest(licenseBytes),
                false),
            []);
        var catalog = new LocalModelDependencyCatalogReview(
            LocalModelDependencyCatalogLoader.CurrentSchemaVersion,
            "fixture-catalog",
            new string('a', 64),
            Array.AsReadOnly([candidate]));
        var plan = new LocalModelDependencyTransferPlanner().Create(
            catalog,
            [candidate.Id],
            long.MaxValue);
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [candidate.SourceUri.AbsoluteUri] = candidateBytes,
            [candidate.License.SourceUri.AbsoluteUri] = licenseBytes,
        };
        var transfer = new MemoryTransfer(content);
        var anchors = new MemoryLocalModelApprovalStateAnchorStore();
        var coordinator = new LocalModelDependencyTransferSetCoordinator(
            fixtureRoot,
            transfer,
            anchors,
            _ => freeBytes);
        return new FixtureState(fixtureRoot, plan, coordinator, transfer, anchors);
    }

    private static string CompletedRoot(FixtureState fixture) => Path.Combine(
        fixture.Root,
        "LocalModels",
        "dependency-transfers",
        fixture.Plan.PlanSha256);

    private static void AssertNoSetOrIncoming(FixtureState fixture)
    {
        var transfers = Path.Combine(fixture.Root, "LocalModels", "dependency-transfers");
        if (!Directory.Exists(transfers)) return;
        Assert.False(Directory.Exists(CompletedRoot(fixture)));
        Assert.Empty(Directory.EnumerateDirectories(transfers, ".incoming-*"));
    }

    private static async Task AssertCodeAsync(string code, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<LocalModelDependencyTransferException>(action);
        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
    }

    private static string Digest(byte[] value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));

    private sealed record FixtureState(
        string Root,
        LocalModelDependencyTransferPlan Plan,
        LocalModelDependencyTransferSetCoordinator Coordinator,
        MemoryTransfer Transfer,
        MemoryLocalModelApprovalStateAnchorStore Anchors);

    private sealed class MemoryTransfer(IReadOnlyDictionary<string, byte[]> content)
        : ILocalModelDependencyTransfer
    {
        public int Calls { get; private set; }
        public int? FailAtCall { get; set; }
        public int? ReturnWrongDigestAtCall { get; set; }
        public int? CancelAtCall { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }

        public async Task<LocalModelDependencyTransferResult> TransferAsync(
            LocalModelDependencyTransferPlan plan,
            LocalModelDependencyTransferApproval approval,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (FailAtCall == Calls) throw new IOException("synthetic private transfer failure");
            if (CancelAtCall == Calls)
            {
                Cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var bytes = content[approval.SourceUri.AbsoluteUri];
            await destination.WriteAsync(bytes, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return new LocalModelDependencyTransferResult(
                plan.PlanSha256,
                approval.SourceUri,
                bytes.LongLength,
                ReturnWrongDigestAtCall == Calls ? new string('0', 64) : Digest(bytes));
        }
    }
}
