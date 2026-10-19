using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class PackageRemovalWorkflowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 20, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Review_BindsExactIdentityConsequencesAndExpiry()
    {
        var review = Review();

        PackageRemovalReviewPolicy.Require(review);
        Assert.Equal(Now, review.ReviewedUtc);
        Assert.Equal(Now + TimeSpan.FromMinutes(10), review.ExpiresUtc);
        Assert.Equal(64, review.ReviewSha256.Length);
        PackageRemovalReviewPolicy.RequireCurrent(review, review.ReviewSha256, Now);

        AssertCode(
            "package_removal_review_changed",
            () => PackageRemovalReviewPolicy.RequireCurrent(
                review,
                new string('0', 64),
                Now));
        AssertCode(
            "package_removal_review_expired",
            () => PackageRemovalReviewPolicy.RequireCurrent(
                review,
                review.ReviewSha256,
                review.ExpiresUtc.AddTicks(1)));
    }

    [Fact]
    public void Review_RejectsEveryIdentityTimeAndDigestMutation()
    {
        var valid = Review();
        var invalid = new[]
        {
            valid with { PackageName = string.Empty },
            valid with { PackageName = "DCx\u202Eexe" },
            valid with { PackageFullName = new string('a', 513) },
            valid with { Publisher = "CN=DCx\nInjected" },
            valid with { PublisherId = string.Empty },
            valid with { Version = "1.2.3" },
            valid with { Version = "01.2.3.4" },
            valid with { Architecture = "AnyCPU" },
            valid with { ReviewedUtc = valid.ReviewedUtc.ToOffset(TimeSpan.FromHours(1)) },
            valid with { ExpiresUtc = valid.ExpiresUtc.AddSeconds(1) },
            valid with { ReviewSha256 = new string('A', 64) },
            valid with { PackageName = "Changed" },
        };

        foreach (var review in invalid)
        {
            AssertCode(
                "package_removal_review_invalid",
                () => PackageRemovalReviewPolicy.Require(review));
        }

        AssertCode(
            "package_removal_review_invalid",
            () => PackageRemovalReviewPolicy.Require(null));
    }

    [Fact]
    public async Task ValidationWorkflow_RequiresBothAcknowledgmentsAndIsSingleUse()
    {
        var review = Review();
        var workflow = new ValidationPackageRemovalWorkflow(review);

        await AssertCodeAsync(
            "package_removal_consequences_acknowledgment_required",
            () => workflow.ExecuteAsync(review.ReviewSha256, Now, false, false, default));
        await AssertCodeAsync(
            "package_removal_backup_acknowledgment_required",
            () => workflow.ExecuteAsync(review.ReviewSha256, Now, true, false, default));

        var result = await workflow.ExecuteAsync(
            review.ReviewSha256,
            Now,
            consequencesAcknowledged: true,
            backupChoiceAcknowledged: true,
            default);

        PackageRemovalWorkflowResultPolicy.Require(review, result);
        Assert.Equal(PackageRemovalExecutionStatus.ValidationNoOp, result.Status);
        Assert.False(result.ExecutionPermitted);
        Assert.Equal(0, result.PackageManagerInvocations);
        Assert.False(result.RemovalCompleted);
        Assert.True(result.IsRegistered);
        Assert.False(result.ApplicationTerminationExpected);
        Assert.Equal(0, result.ProductionActions);

        await AssertCodeAsync(
            "package_removal_request_consumed",
            () => workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, default));
    }

    [Fact]
    public async Task ValidationWorkflow_HonorsCancellationBeforeConsumption()
    {
        var review = Review();
        var workflow = new ValidationPackageRemovalWorkflow(review);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, cancellation.Token));

        var result = await workflow.ExecuteAsync(
            review.ReviewSha256,
            Now,
            true,
            true,
            default);
        Assert.Equal(PackageRemovalExecutionStatus.ValidationNoOp, result.Status);
    }

    [Fact]
    public async Task ApprovedWorkflow_MapsExactTerminalRemovalResult()
    {
        var review = Review();
        var executor = new FixtureRemovalExecutor(() => Task.FromResult(Execution()));
        var workflow = new ApprovedCurrentPackageRemovalWorkflow(review, executor);

        var result = await workflow.ExecuteAsync(
            review.ReviewSha256,
            Now,
            consequencesAcknowledged: true,
            backupChoiceAcknowledged: true,
            default);

        Assert.Equal(1, executor.Calls);
        Assert.Equal(review.PackageFullName, executor.ExpectedPackageFullName);
        PackageRemovalWorkflowResultPolicy.Require(review, result);
        Assert.Equal(PackageRemovalExecutionStatus.Removed, result.Status);
        Assert.True(result.ExecutionPermitted);
        Assert.Equal(1, result.PackageManagerInvocations);
        Assert.True(result.RemovalCompleted);
        Assert.False(result.IsRegistered);
        Assert.True(result.ApplicationTerminationExpected);
        Assert.Equal(1, result.ProductionActions);
    }

    [Fact]
    public async Task ApprovedWorkflow_FailsClosedRedactsAndConsumesAfterEntry()
    {
        var review = Review();
        var executor = new FixtureRemovalExecutor(() =>
            throw new InvalidOperationException("private platform diagnostic"));
        var workflow = new ApprovedCurrentPackageRemovalWorkflow(review, executor);

        var exception = await Assert.ThrowsAsync<PackageRemovalWorkflowException>(() =>
            workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, default));
        Assert.Equal("package_removal_execution_failed", exception.Code);
        Assert.DoesNotContain("private", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, executor.Calls);

        await AssertCodeAsync(
            "package_removal_request_consumed",
            () => workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, default));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task ApprovedWorkflow_SerializesConcurrentOneUseExecution()
    {
        var review = Review();
        var terminal = new TaskCompletionSource<CurrentUserPackageRemovalExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FixtureRemovalExecutor(() => terminal.Task);
        var workflow = new ApprovedCurrentPackageRemovalWorkflow(review, executor);

        var first = workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, default);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, executor.Calls);

        await AssertCodeAsync(
            "package_removal_request_consumed",
            () => workflow.ExecuteAsync(review.ReviewSha256, Now, true, true, default));
        Assert.Equal(1, executor.Calls);

        terminal.SetResult(Execution());
        var result = await first;
        Assert.Equal(PackageRemovalExecutionStatus.Removed, result.Status);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task UnavailableWorkflow_ExposesOnlyClosedUnavailableStates()
    {
        var review = Review();
        var unpackaged = new UnavailablePackageRemovalWorkflow(
            PackageRemovalAvailability.NotPackaged);
        Assert.Null(unpackaged.Review);
        Assert.Equal(PackageRemovalSourceKind.Unavailable, unpackaged.SourceKind);

        var identityBlocked = new UnavailablePackageRemovalWorkflow(
            PackageRemovalAvailability.ProductionIdentityNotConfigured,
            review);
        Assert.Same(review, identityBlocked.Review);
        await AssertCodeAsync(
            "package_removal_unavailable",
            () => identityBlocked.ExecuteAsync(review.ReviewSha256, Now, true, true, default));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new UnavailablePackageRemovalWorkflow(PackageRemovalAvailability.Available));
    }

    [Fact]
    public void ResultPolicy_RejectsEveryUnsafeOrSubstitutedShape()
    {
        var review = Review();
        var valid = new PackageRemovalWorkflowResult(
            PackageRemovalExecutionStatus.ValidationNoOp,
            review.ReviewSha256,
            ExecutionPermitted: false,
            PackageManagerInvocations: 0,
            RemovalCompleted: false,
            IsRegistered: true,
            IsCurrentUserOnly: true,
            TargetsCallingPackage: true,
            ApplicationTerminationExpected: false,
            CancellationSupportedAfterEntry: false,
            RequestsElevation: false,
            InstallerProcessesStarted: 0,
            ProductionActions: 0);
        PackageRemovalWorkflowResultPolicy.Require(review, valid);

        var invalid = new[]
        {
            valid with { Status = (PackageRemovalExecutionStatus)99 },
            valid with { ReviewSha256 = new string('0', 64) },
            valid with { ExecutionPermitted = true },
            valid with { PackageManagerInvocations = 1 },
            valid with { RemovalCompleted = true },
            valid with { IsRegistered = false },
            valid with { IsCurrentUserOnly = false },
            valid with { TargetsCallingPackage = false },
            valid with { ApplicationTerminationExpected = true },
            valid with { CancellationSupportedAfterEntry = true },
            valid with { RequestsElevation = true },
            valid with { InstallerProcessesStarted = 1 },
            valid with { ProductionActions = 1 },
        };
        foreach (var result in invalid)
        {
            AssertCode(
                "package_removal_result_invalid",
                () => PackageRemovalWorkflowResultPolicy.Require(review, result));
        }

        AssertCode(
            "package_removal_result_invalid",
            () => PackageRemovalWorkflowResultPolicy.Require(review, null));
    }

    private static PackageRemovalReview Review() => PackageRemovalReviewPolicy.Create(
        "DCx.AgenticBrowser.ValidationFixture",
        "DCx.AgenticBrowser.ValidationFixture_1.2.3.4_x64__fixture123456",
        "CN=DCx Synthetic Validation Fixture",
        "fixture123456",
        "1.2.3.4",
        "X64",
        Now);

    private static CurrentUserPackageRemovalExecutionResult Execution() => new(
        CurrentUserPackageRemovalExecutionStatus.Removed,
        PackageManagerInvocations: 1,
        IsRegistered: false,
        IsCurrentUserOnly: true,
        TargetsCallingPackage: true,
        ApplicationTerminationExpected: true,
        CancellationSupportedAfterEntry: false,
        RequestsElevation: false,
        InstallerProcessesStarted: 0);

    private static void AssertCode(string expected, Action action)
    {
        var exception = Assert.Throws<PackageRemovalWorkflowException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PackageRemovalWorkflowException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FixtureRemovalExecutor(
        Func<Task<CurrentUserPackageRemovalExecutionResult>> handler)
        : ICurrentUserPackageRemovalExecutor
    {
        public int Calls { get; private set; }

        public string? ExpectedPackageFullName { get; private set; }

        public Task<CurrentUserPackageRemovalExecutionResult> ExecuteAsync(
            string expectedPackageFullName,
            CancellationToken cancellationToken)
        {
            Calls++;
            ExpectedPackageFullName = expectedPackageFullName;
            return handler();
        }
    }
}
