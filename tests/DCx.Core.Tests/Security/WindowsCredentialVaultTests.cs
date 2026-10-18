using DCx.Core.Security;

namespace DCx.Core.Tests.Security;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public void Vault_RoundTripsAndDeletesCredentialForCurrentWindowsUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var vault = new WindowsCredentialVault();
        var target = $"DCx/Test/{Guid.NewGuid():N}";
        const string value = "non-secret-fixture-value";
        try
        {
            vault.Save(target, value);

            Assert.Equal(value, vault.Read(target));
            Assert.True(vault.Delete(target));
            Assert.Null(vault.Read(target));
        }
        finally
        {
            vault.Delete(target);
        }
    }

    [Fact]
    public async Task Vault_ConcurrentUniqueTargetsRemainIsolatedAndAreDeleted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var vault = new WindowsCredentialVault();
        var values = Enumerable.Range(0, 16)
            .Select(index => (
                Target: $"DCx/Test/Concurrent/{Guid.NewGuid():N}",
                Value: $"non-secret-concurrent-fixture-{index}"))
            .ToArray();
        try
        {
            await Task.WhenAll(values.Select(item => Task.Run(() =>
            {
                vault.Save(item.Target, item.Value);
                Assert.Equal(item.Value, vault.Read(item.Target));
            })));

            Assert.All(values, item => Assert.Equal(item.Value, vault.Read(item.Target)));
            await Task.WhenAll(values.Select(item => Task.Run(() =>
                Assert.True(vault.Delete(item.Target)))));
            Assert.All(values, item => Assert.Null(vault.Read(item.Target)));
        }
        finally
        {
            foreach (var item in values) vault.Delete(item.Target);
        }
    }
}
