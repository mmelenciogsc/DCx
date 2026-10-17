using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class PayoutRecipientSnapshotStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"dcx-recipient-snapshots-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");

    [Fact]
    public async Task Approval_IsExplicitProtectedImmutableAndRevocableAcrossReopen()
    {
        var secrets = new MemorySecretStore();
        var store = await PayoutRecipientSnapshotStore.OpenAsync(root, secrets);
        const string contributor = "contributor:alpha";
        const string destination = "alpha@personal.example.com";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ApproveAsync(contributor, destination, false, now));
        Assert.Empty(await store.ListAsync());
        Assert.Empty(secrets.Values);

        var review = PayoutRecipientSnapshotStore.ReviewCandidate(contributor, destination);
        var approved = await store.ApproveAsync(contributor, destination, true, now);
        Assert.True(approved.IsActive);
        Assert.Equal(review.DestinationSha256, approved.DestinationSha256);
        Assert.Equal(approved.SnapshotId, approved.Approval.SnapshotId);
        Assert.Equal(approved.DestinationSha256, approved.Approval.DestinationSha256);
        Assert.DoesNotContain(
            destination,
            await File.ReadAllTextAsync(store.StatePathForTests),
            StringComparison.OrdinalIgnoreCase);

        var reopened = await PayoutRecipientSnapshotStore.OpenAsync(root, secrets);
        var listed = Assert.Single(await reopened.ListAsync());
        Assert.Equal(approved, listed);
        var resolved = (await reopened.ResolveAsync(approved.SnapshotId, CancellationToken.None))!;
        Assert.Equal(destination, resolved.Email);
        Assert.Equal(contributor, resolved.ContributorPseudonym);
        Assert.False(resolved.IsVerifiedAndEligible);
        Assert.Equal(approved.Approval, await reopened.GetOwnerApprovedApprovalAsync(approved.SnapshotId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.ApproveAsync(contributor, "replacement@personal.example.com", true, now.AddMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.ApproveAsync("contributor:other", destination, true, now.AddMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.RevokeAsync(approved.SnapshotId, false, now.AddMinutes(2)));

        await reopened.RevokeAsync(approved.SnapshotId, true, now.AddMinutes(2));
        Assert.Null(await reopened.ResolveAsync(approved.SnapshotId, CancellationToken.None));
        Assert.Null(await reopened.GetOwnerApprovedApprovalAsync(approved.SnapshotId));
        Assert.Empty(secrets.Values);
        var revoked = Assert.Single(await reopened.ListAsync());
        Assert.False(revoked.IsActive);
        Assert.Equal(now.AddMinutes(2), revoked.RevokedUtc);

        var replacement = await reopened.ApproveAsync(
            contributor,
            "replacement@personal.example.com",
            true,
            now.AddMinutes(3));
        Assert.NotEqual(approved.SnapshotId, replacement.SnapshotId);
        Assert.Equal(2, (await reopened.ListAsync()).Count);
    }

    [Fact]
    public async Task ProtectedBindingTamper_FailsClosedWithoutExposingProviderEligibility()
    {
        var secrets = new MemorySecretStore();
        var store = await PayoutRecipientSnapshotStore.OpenAsync(root, secrets);
        var approved = await store.ApproveAsync(
            "contributor:alpha",
            "alpha@personal.example.com",
            true,
            now);
        var target = PayoutRecipientSnapshotStore.TargetName(approved.SnapshotId);
        secrets.Values[target] = secrets.Values[target].Replace(
            "alpha@personal.example.com",
            "other@personal.example.com",
            StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.ResolveAsync(approved.SnapshotId, CancellationToken.None));
        Assert.DoesNotContain("alpha@", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other@", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedProtectionAndInterruptedPendingState_AreRecoveredWithoutApproval()
    {
        var failingSecrets = new MemorySecretStore { FailSave = true };
        var store = await PayoutRecipientSnapshotStore.OpenAsync(root, failingSecrets);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ApproveAsync(
                "contributor:alpha",
                "alpha@personal.example.com",
                true,
                now));
        Assert.Empty(await store.ListAsync());
        Assert.Empty(failingSecrets.Values);

        using (var cancellation = new CancellationTokenSource())
        {
            failingSecrets.FailSave = false;
            failingSecrets.AfterSave = cancellation.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ApproveAsync(
                    "contributor:canceled",
                    "canceled@personal.example.com",
                    true,
                    now,
                    cancellation.Token));
            failingSecrets.AfterSave = null;
        }
        Assert.Empty(await store.ListAsync());
        Assert.Empty(failingSecrets.Values);

        var pendingId = $"recipient:{Guid.NewGuid():N}";
        var target = PayoutRecipientSnapshotStore.TargetName(pendingId);
        failingSecrets.FailSave = false;
        failingSecrets.Save(target, "orphaned fixture secret");
        var destinationDigest = PayoutRecipientSnapshotStore.ReviewCandidate(
            "contributor:pending",
            "pending@personal.example.com").DestinationSha256;
        var state = $$"""
            {
              "schemaVersion": 1,
              "snapshots": [
                {
                  "snapshotId": "{{pendingId}}",
                  "contributorPseudonym": "contributor:pending",
                  "destinationSha256": "{{destinationDigest}}",
                  "credentialTarget": "{{target}}",
                  "state": "pending",
                  "createdUtc": "2026-10-17T12:00:00+00:00",
                  "approvedUtc": null,
                  "revokedUtc": null
                }
              ]
            }
            """;
        await File.WriteAllTextAsync(store.StatePathForTests, state);

        var recovered = await PayoutRecipientSnapshotStore.OpenAsync(root, failingSecrets);
        Assert.Empty(await recovered.ListAsync());
        Assert.False(failingSecrets.Values.ContainsKey(target));
        Assert.DoesNotContain(
            "pending",
            await File.ReadAllTextAsync(recovered.StatePathForTests),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "person@example.com")]
    [InlineData("bad contributor", "person@example.com")]
    [InlineData("contributor:one", "not-an-email")]
    [InlineData("contributor:one", "person@example")]
    [InlineData("contributor:one", "two@@example.com")]
    public void ReviewCandidate_RejectsInvalidIdentityOrDestination(
        string contributor,
        string destination)
    {
        Assert.ThrowsAny<Exception>(() =>
            PayoutRecipientSnapshotStore.ReviewCandidate(contributor, destination));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private sealed class MemorySecretStore : IPayoutRecipientSecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public bool FailSave { get; set; }
        public Action? AfterSave { get; set; }

        public void Save(string targetName, string value)
        {
            if (FailSave) throw new IOException("fixture save failure with private diagnostic");
            Values[targetName] = value;
            AfterSave?.Invoke();
        }

        public string? Read(string targetName) => Values.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => Values.Remove(targetName);
    }
}
