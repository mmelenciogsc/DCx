using DCx.Core.Reliability;
using Windows.Foundation.Metadata;
using Windows.Management.Deployment;

namespace DCx.Desktop.Deployment;

internal sealed class WindowsPackageManagerDeploymentApi : ICurrentUserPackageDeploymentApi
{
    private const string PackageManagerType =
        "Windows.Management.Deployment.PackageManager";

    public async Task<CurrentUserPackageDeploymentResult> AddPackageAsync(
        Uri packageUri,
        CurrentUserPackageDeploymentOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packageUri);
        CurrentUserPackageDeploymentPolicy.Require(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (!packageUri.IsFile ||
            !ApiInformation.IsMethodPresent(PackageManagerType, "AddPackageAsync", 3))
        {
            throw new InvalidOperationException("update_activation_package_manager_unavailable");
        }

        var operation = new PackageManager().AddPackageAsync(
            packageUri,
            Array.Empty<Uri>(),
            DeploymentOptions.None);
        using var cancellation = cancellationToken.Register(operation.Cancel);
        var result = await operation;
        cancellationToken.ThrowIfCancellationRequested();
        return new CurrentUserPackageDeploymentResult(
            result.IsRegistered,
            result.ExtendedErrorCode?.HResult ?? 0);
    }
}
