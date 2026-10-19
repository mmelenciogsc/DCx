using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class CurrentUserPackageActivationExecutorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-package-activation-{Guid.NewGuid():N}");
    private readonly string packagePath;

    public CurrentUserPackageActivationExecutorTests()
    {
        Directory.CreateDirectory(root);
        packagePath = Path.Combine(root, "DCx update fixture.msix");
        File.WriteAllText(packagePath, "synthetic package bytes; never install");
    }

    [Fact]
    public async Task Executor_AwaitsTerminalCurrentUserRegistrationWithExactSafeOptions()
    {
        var terminal = new TaskCompletionSource<CurrentUserPackageDeploymentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FixtureDeploymentApi((_, _, _) => terminal.Task);
        var executor = new CurrentUserPackageActivationExecutor(api);

        var pending = executor.ExecuteAsync(Context(), default);

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, api.Calls);
        Assert.NotNull(api.PackageUri);
        Assert.True(api.PackageUri!.IsFile);
        Assert.Equal(Path.GetFullPath(packagePath), api.PackageUri.LocalPath);
        AssertSafeOptions(api.Options);

        terminal.SetResult(new CurrentUserPackageDeploymentResult(
            IsRegistered: true,
            ExtendedErrorHResult: 0));
        var result = await pending;

        ControlledUpdateActivationExecutionPolicy.Require(result);
        Assert.Equal(ControlledUpdateActivationExecutionStatus.Registered, result.Status);
        Assert.Equal(1, result.PackageManagerInvocations);
        Assert.True(result.IsRegistered);
        Assert.False(result.RestartRequired);
        Assert.False(result.RequestsElevation);
        Assert.Equal(0, result.InstallerProcessesStarted);
    }

    [Fact]
    public async Task Executor_RejectsIncompleteRegistrationAndRedactsApiFailure()
    {
        var incomplete = new CurrentUserPackageActivationExecutor(
            new FixtureDeploymentApi((_, _, _) => Task.FromResult(
                new CurrentUserPackageDeploymentResult(
                    IsRegistered: false,
                    ExtendedErrorHResult: 0))));
        await AssertCodeAsync(
            "update_activation_registration_incomplete",
            () => incomplete.ExecuteAsync(Context(), default));

        var failedResult = new CurrentUserPackageActivationExecutor(
            new FixtureDeploymentApi((_, _, _) => Task.FromResult(
                new CurrentUserPackageDeploymentResult(
                    IsRegistered: false,
                    ExtendedErrorHResult: unchecked((int)0x80073CF9)))));
        await AssertCodeAsync(
            "update_activation_package_manager_failed",
            () => failedResult.ExecuteAsync(Context(), default));

        var failedApi = new CurrentUserPackageActivationExecutor(
            new FixtureDeploymentApi((_, _, _) =>
                throw new InvalidOperationException("private deployment diagnostic")));
        var exception = await Assert.ThrowsAsync<ControlledUpdateException>(() =>
            failedApi.ExecuteAsync(Context(), default));
        Assert.Equal("update_activation_package_manager_failed", exception.Code);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executor_PropagatesCallerCancellationAfterApiEntry()
    {
        var api = new FixtureDeploymentApi(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var executor = new CurrentUserPackageActivationExecutor(api);
        using var cancellation = new CancellationTokenSource();

        var pending = executor.ExecuteAsync(Context(), cancellation.Token);
        Assert.Equal(1, api.Calls);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Executor_RejectsInvalidContextBeforeDeploymentApi()
    {
        var api = new FixtureDeploymentApi((_, _, _) => Task.FromResult(
            new CurrentUserPackageDeploymentResult(true, 0)));
        var executor = new CurrentUserPackageActivationExecutor(api);
        var valid = Context();
        var invalid = new[]
        {
            valid with { PackagePath = Path.Combine(root, "missing.msix") },
            valid with { PackageLength = valid.PackageLength + 1 },
            valid with { Version = "1.2" },
            valid with { PackageSha256 = new string('A', 64) },
            valid with { ReviewSha256 = new string('0', 63) },
            valid with { ReadinessSha256 = new string('g', 64) },
        };

        foreach (var context in invalid)
        {
            await AssertCodeAsync(
                "update_activation_context_invalid",
                () => executor.ExecuteAsync(context, default));
        }

        Assert.Equal(0, api.Calls);
    }

    [Fact]
    public void DeploymentPolicy_RejectsEveryUnsafeOptionAndInvalidResultShape()
    {
        var safe = CurrentUserPackageDeploymentPolicy.CreateOptions();
        CurrentUserPackageDeploymentPolicy.Require(safe);
        var unsafeOptions = new[]
        {
            safe with { DependencyPackageUriCount = 1 },
            safe with { AllowUnsigned = true },
            safe with { DeveloperMode = true },
            safe with { ForceAppShutdown = true },
            safe with { ForceTargetAppShutdown = true },
            safe with { ForceUpdateFromAnyVersion = true },
            safe with { InstallAllResources = true },
            safe with { RequiredContentGroupOnly = true },
            safe with { RetainFilesOnFailure = true },
            safe with { StageInPlace = true },
        };
        foreach (var options in unsafeOptions)
        {
            AssertCode(
                "update_activation_options_invalid",
                () => CurrentUserPackageDeploymentPolicy.Require(options));
        }

        var valid = new ControlledUpdateActivationExecutionResult(
            ControlledUpdateActivationExecutionStatus.Registered,
            PackageManagerInvocations: 1,
            IsRegistered: true,
            RestartRequired: false,
            RequestsElevation: false,
            InstallerProcessesStarted: 0);
        ControlledUpdateActivationExecutionPolicy.Require(valid);
        var invalidResults = new[]
        {
            valid with { PackageManagerInvocations = 0 },
            valid with { IsRegistered = false },
            valid with { RestartRequired = true },
            valid with { RequestsElevation = true },
            valid with { InstallerProcessesStarted = 1 },
            valid with { Status = (ControlledUpdateActivationExecutionStatus)99 },
        };
        foreach (var result in invalidResults)
        {
            AssertCode(
                "update_activation_result_invalid",
                () => ControlledUpdateActivationExecutionPolicy.Require(result));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private ControlledUpdateActivationContext Context() => new(
        Path.GetFullPath(packagePath),
        "1.2.3.4",
        new string('a', 64),
        new FileInfo(packagePath).Length,
        new string('b', 64),
        new string('c', 64));

    private static void AssertSafeOptions(CurrentUserPackageDeploymentOptions? options)
    {
        Assert.NotNull(options);
        CurrentUserPackageDeploymentPolicy.Require(options);
        Assert.Equal(0, options.DependencyPackageUriCount);
        Assert.False(options.AllowUnsigned);
        Assert.False(options.DeveloperMode);
        Assert.False(options.ForceAppShutdown);
        Assert.False(options.ForceTargetAppShutdown);
        Assert.False(options.ForceUpdateFromAnyVersion);
        Assert.False(options.InstallAllResources);
        Assert.False(options.RequiredContentGroupOnly);
        Assert.False(options.RetainFilesOnFailure);
        Assert.False(options.StageInPlace);
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

    private sealed class FixtureDeploymentApi(
        Func<Uri, CurrentUserPackageDeploymentOptions, CancellationToken,
            Task<CurrentUserPackageDeploymentResult>> handler)
        : ICurrentUserPackageDeploymentApi
    {
        public int Calls { get; private set; }
        public Uri? PackageUri { get; private set; }
        public CurrentUserPackageDeploymentOptions? Options { get; private set; }

        public Task<CurrentUserPackageDeploymentResult> AddPackageAsync(
            Uri packageUri,
            CurrentUserPackageDeploymentOptions options,
            CancellationToken cancellationToken)
        {
            Calls++;
            PackageUri = packageUri;
            Options = options;
            return handler(packageUri, options, cancellationToken);
        }
    }
}
