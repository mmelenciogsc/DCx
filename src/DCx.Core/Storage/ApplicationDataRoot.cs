namespace DCx.Core.Storage;

public static class ApplicationDataRoot
{
    public const string OverrideEnvironmentVariable = "DCX_DATA_ROOT";

    public static string Resolve(string? localApplicationData = null)
    {
        var configured = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var root = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, ProductIdentity.CompanyDataDirectoryName);
    }
}

