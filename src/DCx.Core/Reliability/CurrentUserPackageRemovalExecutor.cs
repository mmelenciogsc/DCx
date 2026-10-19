namespace DCx.Core.Reliability;

public sealed record CurrentUserPackageRemovalOptions(
    bool DeferRemovalWhenPackagesAreInUse,
    bool PreserveApplicationData,
    bool PreserveRoamableApplicationData,
    bool RemoveForAllUsers);

public sealed record CurrentUserPackageRemovalResult(
    bool IsRegistered,
    int ExtendedErrorHResult);

/// <summary>
/// Removes only the package identity of the process implementing this API. The expected full
/// name is an equality constraint, not a caller-selected target. Windows does not support
/// cancellation after removal begins.
/// </summary>
public interface ICurrentUserPackageRemovalApi
{
    Task<CurrentUserPackageRemovalResult> RemoveCurrentPackageAsync(
        string expectedPackageFullName,
        CurrentUserPackageRemovalOptions options);
}

public static class CurrentUserPackageRemovalPolicy
{
    public static CurrentUserPackageRemovalOptions CreateOptions() => new(
        DeferRemovalWhenPackagesAreInUse: false,
        PreserveApplicationData: false,
        PreserveRoamableApplicationData: false,
        RemoveForAllUsers: false);

    public static void Require(CurrentUserPackageRemovalOptions? options)
    {
        if (options is null || options.DeferRemovalWhenPackagesAreInUse ||
            options.PreserveApplicationData || options.PreserveRoamableApplicationData ||
            options.RemoveForAllUsers)
        {
            throw new ControlledUpdateException("package_removal_options_invalid");
        }
    }
}

public enum CurrentUserPackageRemovalExecutionStatus
{
    Removed,
}

public sealed record CurrentUserPackageRemovalExecutionResult(
    CurrentUserPackageRemovalExecutionStatus Status,
    int PackageManagerInvocations,
    bool IsRegistered,
    bool IsCurrentUserOnly,
    bool TargetsCallingPackage,
    bool ApplicationTerminationExpected,
    bool CancellationSupportedAfterEntry,
    bool RequestsElevation,
    int InstallerProcessesStarted);

public static class CurrentUserPackageRemovalExecutionPolicy
{
    public static void Require(CurrentUserPackageRemovalExecutionResult? result)
    {
        if (result is null || result.Status != CurrentUserPackageRemovalExecutionStatus.Removed ||
            result.PackageManagerInvocations != 1 || result.IsRegistered ||
            !result.IsCurrentUserOnly || !result.TargetsCallingPackage ||
            !result.ApplicationTerminationExpected || result.CancellationSupportedAfterEntry ||
            result.RequestsElevation || result.InstallerProcessesStarted != 0)
        {
            throw new ControlledUpdateException("package_removal_result_invalid");
        }
    }
}

/// <summary>
/// Core policy around the internal Windows removal adapter. Cancellation is honored only
/// before API entry because Windows documents RemovePackageAsync as noncancellable. DCx does
/// not currently compose or expose this executor.
/// </summary>
public interface ICurrentUserPackageRemovalExecutor
{
    Task<CurrentUserPackageRemovalExecutionResult> ExecuteAsync(
        string expectedPackageFullName,
        CancellationToken cancellationToken);
}

public sealed class CurrentUserPackageRemovalExecutor(
    ICurrentUserPackageRemovalApi removalApi)
    : ICurrentUserPackageRemovalExecutor
{
    private readonly ICurrentUserPackageRemovalApi removalApi =
        removalApi ?? throw new ArgumentNullException(nameof(removalApi));

    public async Task<CurrentUserPackageRemovalExecutionResult> ExecuteAsync(
        string expectedPackageFullName,
        CancellationToken cancellationToken)
    {
        if (!IsBoundedPackageFullName(expectedPackageFullName))
        {
            throw new ControlledUpdateException("package_removal_identity_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = CurrentUserPackageRemovalPolicy.CreateOptions();
        CurrentUserPackageRemovalPolicy.Require(options);

        CurrentUserPackageRemovalResult? result;
        try
        {
            result = await removalApi.RemoveCurrentPackageAsync(
                    expectedPackageFullName,
                    options)
                .ConfigureAwait(false);
        }
        catch
        {
            throw new ControlledUpdateException("package_removal_package_manager_failed");
        }

        if (result is null || result.ExtendedErrorHResult != 0)
        {
            throw new ControlledUpdateException("package_removal_package_manager_failed");
        }

        if (result.IsRegistered)
        {
            throw new ControlledUpdateException("package_removal_incomplete");
        }

        var execution = new CurrentUserPackageRemovalExecutionResult(
            CurrentUserPackageRemovalExecutionStatus.Removed,
            PackageManagerInvocations: 1,
            IsRegistered: false,
            IsCurrentUserOnly: true,
            TargetsCallingPackage: true,
            ApplicationTerminationExpected: true,
            CancellationSupportedAfterEntry: false,
            RequestsElevation: false,
            InstallerProcessesStarted: 0);
        CurrentUserPackageRemovalExecutionPolicy.Require(execution);
        return execution;
    }

    private static bool IsBoundedPackageFullName(string? value) =>
        value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) &&
        value.All(character => !char.IsControl(character));
}
