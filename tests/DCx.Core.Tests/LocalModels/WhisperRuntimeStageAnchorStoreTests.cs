using System.Security.Cryptography;
using System.Text;
using DCx.Core.LocalModels;
using DCx.Core.Security;

namespace DCx.Core.Tests.LocalModels;

public sealed class WhisperRuntimeStageAnchorStoreTests
{
    private static readonly DateTimeOffset StagedUtc =
        new(2026, 10, 18, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task WindowsVaultAnchor_RoundTripsIsolatesAndRejectsConflictUnknownAndDuplicateState()
    {
        if (!OperatingSystem.IsWindows()) return;

        var candidate = RuntimeCandidate(
            "test-anchor-" + Guid.NewGuid().ToString("N"),
            Digest("archive-one"));
        var otherCandidate = RuntimeCandidate(
            candidate.Id,
            Digest("archive-two"));
        var anchor = new WhisperRuntimeStageAnchor(
            WindowsWhisperRuntimeStageAnchorStore.CurrentSchemaVersion,
            candidate.Id,
            candidate.Sha256,
            Digest("receipt-one"),
            512,
            StagedUtc);
        var store = new WindowsWhisperRuntimeStageAnchorStore();
        var vault = new WindowsCredentialVault();
        var target = WindowsWhisperRuntimeStageAnchorStore.TargetName(
            candidate.Id,
            candidate.Sha256);
        var otherTarget = WindowsWhisperRuntimeStageAnchorStore.TargetName(
            otherCandidate.Id,
            otherCandidate.Sha256);
        try
        {
            Assert.Null(await store.ReadAsync(candidate));
            Assert.Null(await store.ReadAsync(otherCandidate));

            await store.BindAsync(anchor);
            Assert.Equal(anchor, await store.ReadAsync(candidate));
            Assert.Null(await store.ReadAsync(otherCandidate));
            await store.BindAsync(anchor);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.BindAsync(
                anchor with { ReceiptSha256 = Digest("conflict") }));
            Assert.Equal(anchor, await store.ReadAsync(candidate));

            vault.Save(
                target,
                $"{{\"schemaVersion\":1,\"candidateId\":\"{candidate.Id}\"," +
                $"\"archiveSha256\":\"{candidate.Sha256}\",\"receiptSha256\":\"{anchor.ReceiptSha256}\"," +
                $"\"receiptBytes\":512,\"stagedUtc\":\"{StagedUtc:O}\",\"unknown\":true}}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(candidate));

            vault.Save(
                target,
                $"{{\"schemaVersion\":1,\"schemaVersion\":1,\"candidateId\":\"{candidate.Id}\"," +
                $"\"archiveSha256\":\"{candidate.Sha256}\",\"receiptSha256\":\"{anchor.ReceiptSha256}\"," +
                $"\"receiptBytes\":512,\"stagedUtc\":\"{StagedUtc:O}\"}}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(candidate));
        }
        finally
        {
            vault.Delete(target);
            vault.Delete(otherTarget);
        }
    }

    [Fact]
    public async Task WindowsVaultAnchor_HonorsPreCanceledReadAndBindWithoutCreatingCredential()
    {
        if (!OperatingSystem.IsWindows()) return;

        var candidate = RuntimeCandidate(
            "test-anchor-" + Guid.NewGuid().ToString("N"),
            Digest("archive-canceled"));
        var anchor = new WhisperRuntimeStageAnchor(
            WindowsWhisperRuntimeStageAnchorStore.CurrentSchemaVersion,
            candidate.Id,
            candidate.Sha256,
            Digest("receipt-canceled"),
            256,
            StagedUtc);
        var store = new WindowsWhisperRuntimeStageAnchorStore();
        var vault = new WindowsCredentialVault();
        var target = WindowsWhisperRuntimeStageAnchorStore.TargetName(
            candidate.Id,
            candidate.Sha256);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadAsync(candidate, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.BindAsync(anchor, cancellation.Token));
            Assert.Null(vault.Read(target));
        }
        finally
        {
            vault.Delete(target);
        }
    }

    private static PinnedLocalModelDependencyCandidate RuntimeCandidate(
        string id,
        string archiveSha256) => new(
            id,
            LocalModelDependencyKind.WhisperRuntimeArchive,
            "Protected anchor test runtime",
            new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/test/runtime.zip"),
            1024,
            archiveSha256,
            new LocalModelDependencyLicense(
                "MIT plus contained notices",
                new Uri("https://raw.githubusercontent.com/ggml-org/whisper.cpp/test/LICENSE"),
                100,
                new string('a', 64),
                true),
            []);

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
