namespace DCx.Core.Reliability;

public sealed record CurrentUserPackageDeploymentOptions(
    int DependencyPackageUriCount,
    bool AllowUnsigned,
    bool DeveloperMode,
    bool ForceAppShutdown,
    bool ForceTargetAppShutdown,
    bool ForceUpdateFromAnyVersion,
    bool InstallAllResources,
    bool RequiredContentGroupOnly,
    bool RetainFilesOnFailure,
    bool StageInPlace);

public sealed record CurrentUserPackageDeploymentResult(
    bool IsRegistered,
    int ExtendedErrorHResult);

public interface ICurrentUserPackageDeploymentApi
{
    Task<CurrentUserPackageDeploymentResult> AddPackageAsync(
        Uri packageUri,
        CurrentUserPackageDeploymentOptions options,
        CancellationToken cancellationToken);
}

public static class CurrentUserPackageDeploymentPolicy
{
    public static CurrentUserPackageDeploymentOptions CreateOptions() => new(
        DependencyPackageUriCount: 0,
        AllowUnsigned: false,
        DeveloperMode: false,
        ForceAppShutdown: false,
        ForceTargetAppShutdown: false,
        ForceUpdateFromAnyVersion: false,
        InstallAllResources: false,
        RequiredContentGroupOnly: false,
        RetainFilesOnFailure: false,
        StageInPlace: false);

    public static void Require(CurrentUserPackageDeploymentOptions? options)
    {
        if (options is null || options.DependencyPackageUriCount != 0 ||
            options.AllowUnsigned || options.DeveloperMode ||
            options.ForceAppShutdown || options.ForceTargetAppShutdown ||
            options.ForceUpdateFromAnyVersion || options.InstallAllResources ||
            options.RequiredContentGroupOnly || options.RetainFilesOnFailure ||
            options.StageInPlace)
        {
            throw new ControlledUpdateException("update_activation_options_invalid");
        }
    }
}

public sealed class CurrentUserPackageActivationExecutor(
    ICurrentUserPackageDeploymentApi deploymentApi)
    : IControlledUpdateActivationExecutor
{
    private const long MaximumPackageBytes = 1024L * 1024 * 1024;

    private readonly ICurrentUserPackageDeploymentApi deploymentApi =
        deploymentApi ?? throw new ArgumentNullException(nameof(deploymentApi));

    public async Task<ControlledUpdateActivationExecutionResult> ExecuteAsync(
        ControlledUpdateActivationContext context,
        CancellationToken cancellationToken)
    {
        var packageUri = ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();
        var options = CurrentUserPackageDeploymentPolicy.CreateOptions();
        CurrentUserPackageDeploymentPolicy.Require(options);

        CurrentUserPackageDeploymentResult? result;
        try
        {
            result = await deploymentApi.AddPackageAsync(
                    packageUri,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new ControlledUpdateException("update_activation_package_manager_failed");
        }

        if (result is null || result.ExtendedErrorHResult != 0)
        {
            throw new ControlledUpdateException("update_activation_package_manager_failed");
        }

        if (!result.IsRegistered)
        {
            throw new ControlledUpdateException("update_activation_registration_incomplete");
        }

        var execution = new ControlledUpdateActivationExecutionResult(
            ControlledUpdateActivationExecutionStatus.Registered,
            PackageManagerInvocations: 1,
            IsRegistered: true,
            ApplicationCloseRequired: true,
            ApplicationRelaunchRequired: true,
            RestartRequired: false,
            RequestsElevation: false,
            InstallerProcessesStarted: 0);
        ControlledUpdateActivationExecutionPolicy.Require(execution);
        return execution;
    }

    private static Uri ValidateContext(ControlledUpdateActivationContext? context)
    {
        if (context is null || context.PackageLength is <= 0 or > MaximumPackageBytes ||
            !IsLowerSha256(context.PackageSha256) ||
            !IsLowerSha256(context.ReviewSha256) ||
            !IsLowerSha256(context.ReadinessSha256))
        {
            throw new ControlledUpdateException("update_activation_context_invalid");
        }

        try
        {
            _ = ControlledUpdateManifestCodec.ParsePackageVersion(context.Version);
        }
        catch (ControlledUpdateException)
        {
            throw new ControlledUpdateException("update_activation_context_invalid");
        }

        try
        {
            var packagePath = Path.GetFullPath(context.PackagePath);
            if (!Path.IsPathFullyQualified(packagePath) ||
                !string.Equals(Path.GetExtension(packagePath), ".msix", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(packagePath) || Directory.Exists(packagePath) ||
                new FileInfo(packagePath).Length != context.PackageLength ||
                (File.GetAttributes(packagePath) & FileAttributes.ReparsePoint) != 0 ||
                OperatingSystem.IsWindows() &&
                (packagePath.StartsWith("\\\\", StringComparison.Ordinal) ||
                 packagePath.AsSpan(Math.Min(2, packagePath.Length)).Contains(':')))
            {
                throw new ControlledUpdateException("update_activation_context_invalid");
            }

            for (var current = new FileInfo(packagePath).Directory;
                 current is not null;
                 current = current.Parent)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ControlledUpdateException("update_activation_context_invalid");
                }
            }

            var packageUri = new Uri(packagePath, UriKind.Absolute);
            if (!packageUri.IsFile || !string.IsNullOrEmpty(packageUri.Query) ||
                !string.IsNullOrEmpty(packageUri.Fragment) ||
                !string.IsNullOrEmpty(packageUri.UserInfo))
            {
                throw new ControlledUpdateException("update_activation_context_invalid");
            }

            return packageUri;
        }
        catch (ControlledUpdateException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
                PathTooLongException or UnauthorizedAccessException or UriFormatException)
        {
            throw new ControlledUpdateException("update_activation_context_invalid");
        }
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
