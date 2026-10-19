using DCx.Core.Reliability;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Management.Deployment;

namespace DCx.Desktop.Deployment;

internal sealed class WindowsCurrentUserPackageRemovalApi : ICurrentUserPackageRemovalApi
{
    private const string PackageManagerType =
        "Windows.Management.Deployment.PackageManager";
    private const int MaximumPackageFullNameLength = 512;

    public async Task<CurrentUserPackageRemovalResult> RemoveCurrentPackageAsync(
        string expectedPackageFullName,
        CurrentUserPackageRemovalOptions options)
    {
        CurrentUserPackageRemovalPolicy.Require(options);
        if (!ApiInformation.IsMethodPresent(PackageManagerType, "RemovePackageAsync", 2))
        {
            throw new InvalidOperationException("package_removal_package_manager_unavailable");
        }

        string packageFullName;
        try
        {
            packageFullName = Package.Current.Id.FullName;
        }
        catch
        {
            throw new InvalidOperationException("package_removal_identity_unavailable");
        }

        if (string.IsNullOrWhiteSpace(packageFullName) ||
            packageFullName.Length > MaximumPackageFullNameLength ||
            packageFullName.Any(char.IsControl) ||
            !string.Equals(packageFullName, expectedPackageFullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("package_removal_identity_invalid");
        }

        var operation = new PackageManager().RemovePackageAsync(
            packageFullName,
            RemovalOptions.None);
        var result = await operation;
        return new CurrentUserPackageRemovalResult(
            result.IsRegistered,
            result.ExtendedErrorCode?.HResult ?? 0);
    }
}
