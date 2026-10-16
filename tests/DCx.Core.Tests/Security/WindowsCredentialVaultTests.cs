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
}
