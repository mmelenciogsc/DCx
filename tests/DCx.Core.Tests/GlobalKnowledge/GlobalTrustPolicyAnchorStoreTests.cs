using System.Security.Cryptography;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Security;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class GlobalTrustPolicyAnchorStoreTests
{
    [Fact]
    public async Task WindowsVaultAnchor_RoundTripsIsolatesAndRejectsRollbackConflictAndUnknownState()
    {
        if (!OperatingSystem.IsWindows()) return;

        var trustId = Digest("trust-" + Guid.NewGuid().ToString("N"));
        var otherTrustId = Digest("other-" + Guid.NewGuid().ToString("N"));
        var firstPolicy = Digest("policy-one");
        var secondPolicy = Digest("policy-two");
        var vault = new WindowsCredentialVault();
        var store = new WindowsGlobalTrustPolicyAnchorStore();
        var target = WindowsGlobalTrustPolicyAnchorStore.TargetName(trustId);
        try
        {
            var first = new GlobalTrustPolicyAnchor(1, trustId, 1, firstPolicy);
            var second = new GlobalTrustPolicyAnchor(1, trustId, 2, secondPolicy);
            await store.AdvanceAsync(first);
            Assert.Equal(first, await store.ReadAsync(trustId));
            Assert.Null(await store.ReadAsync(otherTrustId));

            await store.AdvanceAsync(second);
            Assert.Equal(second, await store.ReadAsync(trustId));
            await Assert.ThrowsAsync<GlobalReplicaException>(() => store.AdvanceAsync(first));
            await Assert.ThrowsAsync<GlobalReplicaException>(() => store.AdvanceAsync(
                second with { PolicySha256 = Digest("conflict") }));

            vault.Save(
                target,
                $"{{\"schemaVersion\":1,\"trustRootSha256\":\"{trustId}\",\"revision\":2," +
                $"\"policySha256\":\"{secondPolicy}\",\"unknown\":true}}");
            await Assert.ThrowsAsync<GlobalReplicaException>(() => store.ReadAsync(trustId));
        }
        finally
        {
            vault.Delete(target);
            vault.Delete(WindowsGlobalTrustPolicyAnchorStore.TargetName(otherTrustId));
        }
    }

    [Fact]
    public async Task DefaultReplicaClient_UsesWindowsVaultAnchorAgainstFilesystemRollback()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var trustRoot = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trustBytes = trustRoot.ExportSubjectPublicKeyInfo();
        var trustId = GlobalReleaseCodec.Sha256(trustBytes);
        var releaseBytes = releaseKey.ExportSubjectPublicKeyInfo();
        var authorization = new GlobalReleaseKeyAuthorization(
            GlobalReleaseCodec.Sha256(releaseBytes),
            Convert.ToBase64String(releaseBytes),
            1);
        var policyOne = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1, 1, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"), [authorization]),
            trustRoot));
        var policyTwo = GlobalTrustPolicyCodec.Encode(GlobalTrustPolicyCodec.Sign(
            new GlobalTrustPolicy(
                1,
                2,
                GlobalReleaseCodec.Sha256(policyOne),
                DateTimeOffset.Parse("2026-10-18T00:00:00Z"),
                [authorization]),
            trustRoot));
        var dataRoot = Path.Combine(Path.GetTempPath(), $"dcx-p09-windows-anchor-{Guid.NewGuid():N}");
        var vault = new WindowsCredentialVault();
        try
        {
            var client = await GlobalReplicaClient.OpenAsync(dataRoot, trustBytes);
            await client.ApplyTrustPolicyAsync(policyOne);
            await client.ApplyTrustPolicyAsync(policyTwo);
            await File.WriteAllBytesAsync(Path.Combine(client.ReplicaRoot, "trust-policy.json"), policyOne);

            await Assert.ThrowsAsync<GlobalReplicaException>(() =>
                GlobalReplicaClient.OpenAsync(dataRoot, trustBytes));
        }
        finally
        {
            vault.Delete(WindowsGlobalTrustPolicyAnchorStore.TargetName(trustId));
            TestDirectory.DeleteIfExists(dataRoot);
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
