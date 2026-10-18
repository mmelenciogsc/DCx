using System.Security.Cryptography;
using System.Text;
using DCx.Core.LocalModels;
using DCx.Core.Security;

namespace DCx.Core.Tests.LocalModels;

public sealed class LocalModelApprovalStateAnchorStoreTests
{
    [Fact]
    public async Task WindowsVaultAnchor_RoundTripsOverwritesIsolatesAndRejectsMalformedState()
    {
        if (!OperatingSystem.IsWindows()) return;

        var stateKind = "test-selection";
        var storageId = Digest("storage-" + Guid.NewGuid().ToString("N"));
        var otherStorageId = Digest("other-" + Guid.NewGuid().ToString("N"));
        var first = new LocalModelApprovalStateAnchor(
            WindowsLocalModelApprovalStateAnchorStore.CurrentSchemaVersion,
            stateKind,
            storageId,
            Digest("state-one"),
            512);
        var second = first with { StateSha256 = Digest("state-two"), StateBytes = 768 };
        var store = new WindowsLocalModelApprovalStateAnchorStore();
        var vault = new WindowsCredentialVault();
        var target = WindowsLocalModelApprovalStateAnchorStore.TargetName(stateKind, storageId);
        var otherTarget = WindowsLocalModelApprovalStateAnchorStore.TargetName(stateKind, otherStorageId);
        try
        {
            Assert.Null(await store.ReadAsync(stateKind, storageId));
            await store.WriteAsync(first);
            Assert.Equal(first, await store.ReadAsync(stateKind, storageId));
            Assert.Null(await store.ReadAsync(stateKind, otherStorageId));

            await store.WriteAsync(second);
            Assert.Equal(second, await store.ReadAsync(stateKind, storageId));

            vault.Save(
                target,
                $"{{\"schemaVersion\":1,\"stateKind\":\"{stateKind}\"," +
                $"\"storageId\":\"{storageId}\",\"stateSha256\":\"{second.StateSha256}\"," +
                "\"stateBytes\":768,\"unknown\":true}");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadAsync(stateKind, storageId));

            vault.Save(
                target,
                $"{{\"schemaVersion\":1,\"schemaVersion\":1,\"stateKind\":\"{stateKind}\"," +
                $"\"storageId\":\"{storageId}\",\"stateSha256\":\"{second.StateSha256}\"," +
                "\"stateBytes\":768}");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadAsync(stateKind, storageId));

            await store.DeleteAsync(stateKind, storageId);
            Assert.Null(await store.ReadAsync(stateKind, storageId));
        }
        finally
        {
            vault.Delete(target);
            vault.Delete(otherTarget);
        }
    }

    [Fact]
    public async Task WindowsVaultAnchor_HonorsPreCanceledOperationsWithoutCreatingCredential()
    {
        if (!OperatingSystem.IsWindows()) return;

        var stateKind = "test-selection";
        var storageId = Digest("canceled-" + Guid.NewGuid().ToString("N"));
        var anchor = new LocalModelApprovalStateAnchor(
            WindowsLocalModelApprovalStateAnchorStore.CurrentSchemaVersion,
            stateKind,
            storageId,
            Digest("state"),
            128);
        var store = new WindowsLocalModelApprovalStateAnchorStore();
        var vault = new WindowsCredentialVault();
        var target = WindowsLocalModelApprovalStateAnchorStore.TargetName(stateKind, storageId);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadAsync(stateKind, storageId, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.WriteAsync(anchor, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DeleteAsync(stateKind, storageId, cancellation.Token));
            Assert.Null(vault.Read(target));
        }
        finally
        {
            vault.Delete(target);
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
