using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class CurrentUserPackageRemovalExecutorTests
{
    [Fact]
    public async Task Executor_AwaitsTerminalCurrentUserRemovalWithExactSafeOptions()
    {
        var terminal = new TaskCompletionSource<CurrentUserPackageRemovalResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FixtureRemovalApi((_, _) => terminal.Task);
        var executor = new CurrentUserPackageRemovalExecutor(api);
        using var cancellation = new CancellationTokenSource();

        var pending = executor.ExecuteAsync(PackageFullName, cancellation.Token);

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, api.Calls);
        Assert.Equal(PackageFullName, api.ExpectedPackageFullName);
        AssertSafeOptions(api.Options);

        cancellation.Cancel();
        Assert.False(pending.IsCompleted);
        terminal.SetResult(new CurrentUserPackageRemovalResult(
            IsRegistered: false,
            ExtendedErrorHResult: 0));
        var result = await pending;

        CurrentUserPackageRemovalExecutionPolicy.Require(result);
        Assert.Equal(CurrentUserPackageRemovalExecutionStatus.Removed, result.Status);
        Assert.Equal(1, result.PackageManagerInvocations);
        Assert.False(result.IsRegistered);
        Assert.True(result.IsCurrentUserOnly);
        Assert.True(result.TargetsCallingPackage);
        Assert.True(result.ApplicationTerminationExpected);
        Assert.False(result.CancellationSupportedAfterEntry);
        Assert.False(result.RequestsElevation);
        Assert.Equal(0, result.InstallerProcessesStarted);
    }

    [Fact]
    public async Task Executor_HonorsCancellationOnlyBeforePackageManagerEntry()
    {
        var api = new FixtureRemovalApi((_, _) => Task.FromResult(
            new CurrentUserPackageRemovalResult(false, 0)));
        var executor = new CurrentUserPackageRemovalExecutor(api);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteAsync(PackageFullName, cancellation.Token));

        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public async Task Executor_RejectsIncompleteRemovalAndRedactsApiFailure()
    {
        var incomplete = new CurrentUserPackageRemovalExecutor(
            new FixtureRemovalApi((_, _) => Task.FromResult(
                new CurrentUserPackageRemovalResult(
                    IsRegistered: true,
                    ExtendedErrorHResult: 0))));
        await AssertCodeAsync(
            "package_removal_incomplete",
            () => incomplete.ExecuteAsync(PackageFullName, default));

        var failedResult = new CurrentUserPackageRemovalExecutor(
            new FixtureRemovalApi((_, _) => Task.FromResult(
                new CurrentUserPackageRemovalResult(
                    IsRegistered: true,
                    ExtendedErrorHResult: unchecked((int)0x80073CF1)))));
        await AssertCodeAsync(
            "package_removal_package_manager_failed",
            () => failedResult.ExecuteAsync(PackageFullName, default));

        var failedApi = new CurrentUserPackageRemovalExecutor(
            new FixtureRemovalApi((_, _) =>
                throw new InvalidOperationException("private removal diagnostic")));
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(() =>
            failedApi.ExecuteAsync(PackageFullName, default));
        Assert.Equal("package_removal_package_manager_failed", exception.Code);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovalPolicy_RejectsEveryUnsafeOptionAndInvalidResultShape()
    {
        var safe = CurrentUserPackageRemovalPolicy.CreateOptions();
        CurrentUserPackageRemovalPolicy.Require(safe);
        AssertCode(
            "package_removal_options_invalid",
            () => CurrentUserPackageRemovalPolicy.Require(null));
        var unsafeOptions = new[]
        {
            safe with { DeferRemovalWhenPackagesAreInUse = true },
            safe with { PreserveApplicationData = true },
            safe with { PreserveRoamableApplicationData = true },
            safe with { RemoveForAllUsers = true },
        };
        foreach (var options in unsafeOptions)
        {
            AssertCode(
                "package_removal_options_invalid",
                () => CurrentUserPackageRemovalPolicy.Require(options));
        }

        var valid = new CurrentUserPackageRemovalExecutionResult(
            CurrentUserPackageRemovalExecutionStatus.Removed,
            PackageManagerInvocations: 1,
            IsRegistered: false,
            IsCurrentUserOnly: true,
            TargetsCallingPackage: true,
            ApplicationTerminationExpected: true,
            CancellationSupportedAfterEntry: false,
            RequestsElevation: false,
            InstallerProcessesStarted: 0);
        CurrentUserPackageRemovalExecutionPolicy.Require(valid);
        AssertCode(
            "package_removal_result_invalid",
            () => CurrentUserPackageRemovalExecutionPolicy.Require(null));
        var invalidResults = new[]
        {
            valid with { Status = (CurrentUserPackageRemovalExecutionStatus)99 },
            valid with { PackageManagerInvocations = 0 },
            valid with { PackageManagerInvocations = 2 },
            valid with { IsRegistered = true },
            valid with { IsCurrentUserOnly = false },
            valid with { TargetsCallingPackage = false },
            valid with { ApplicationTerminationExpected = false },
            valid with { CancellationSupportedAfterEntry = true },
            valid with { RequestsElevation = true },
            valid with { InstallerProcessesStarted = 1 },
        };
        foreach (var result in invalidResults)
        {
            AssertCode(
                "package_removal_result_invalid",
                () => CurrentUserPackageRemovalExecutionPolicy.Require(result));
        }
    }

    [Fact]
    public async Task Executor_RejectsInvalidExpectedIdentityBeforePackageManagerEntry()
    {
        var api = new FixtureRemovalApi((_, _) => Task.FromResult(
            new CurrentUserPackageRemovalResult(false, 0)));
        var executor = new CurrentUserPackageRemovalExecutor(api);
        foreach (var invalid in new[]
                 {
                     string.Empty,
                     " ",
                     "DCx\nInjected",
                     new string('a', 513),
                 })
        {
            await AssertCodeAsync(
                "package_removal_identity_invalid",
                () => executor.ExecuteAsync(invalid, default));
        }

        Assert.Equal(0, api.Calls);
    }

    private static void AssertSafeOptions(CurrentUserPackageRemovalOptions? options)
    {
        Assert.NotNull(options);
        CurrentUserPackageRemovalPolicy.Require(options);
        Assert.False(options.DeferRemovalWhenPackagesAreInUse);
        Assert.False(options.PreserveApplicationData);
        Assert.False(options.PreserveRoamableApplicationData);
        Assert.False(options.RemoveForAllUsers);
    }

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private const string PackageFullName =
        "DCx.AgenticBrowser.ValidationFixture_1.2.3.4_x64__fixture123456";

    private sealed class FixtureRemovalApi(
        Func<string, CurrentUserPackageRemovalOptions, Task<CurrentUserPackageRemovalResult>> handler)
        : ICurrentUserPackageRemovalApi
    {
        public int Calls { get; private set; }
        public string? ExpectedPackageFullName { get; private set; }
        public CurrentUserPackageRemovalOptions? Options { get; private set; }

        public Task<CurrentUserPackageRemovalResult> RemoveCurrentPackageAsync(
            string expectedPackageFullName,
            CurrentUserPackageRemovalOptions options)
        {
            Calls++;
            ExpectedPackageFullName = expectedPackageFullName;
            Options = options;
            return handler(expectedPackageFullName, options);
        }
    }
}
