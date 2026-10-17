using DCx.Core.Deployment;

namespace DCx.Core.Tests.Deployment;

public sealed class PackageInstallReviewTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 17, 10, 0, 0, TimeSpan.Zero);

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"dcx-package-review-{Guid.NewGuid():N}");
    private readonly string packagePath;

    public PackageInstallReviewTests()
    {
        Directory.CreateDirectory(root);
        packagePath = Path.Combine(root, "DCx fixture's package.msix");
        File.WriteAllText(packagePath, "synthetic signed MSIX fixture; never execute");
    }

    [Fact]
    public async Task Begin_CreatesDigestBoundTypedCurrentUserProposal()
    {
        var verifier = new FixtureTrustVerifier();

        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            verifier,
            Now);

        var proposal = session.Proposal;
        Assert.Equal(PackageInstallReviewService.CurrentSchemaVersion, proposal.SchemaVersion);
        Assert.Equal(PackageInstallReviewService.CurrentUserMsixOperation, proposal.OperationKind);
        Assert.Equal(PackageInstallReviewService.DeploymentApiName, proposal.DeploymentApi);
        Assert.Equal("None", proposal.DeploymentOptions);
        Assert.Equal("Current Windows user", proposal.Scope);
        Assert.False(proposal.RequestsElevation);
        Assert.Equal(Path.GetFullPath(packagePath), proposal.PackagePath);
        Assert.StartsWith("file:", proposal.PackageUri, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(packagePath), proposal.PackageFileName);
        Assert.Equal(64, proposal.PackageSha256.Length);
        Assert.Equal(64, proposal.ProposalSha256.Length);
        Assert.Equal(Now.AddMinutes(10), proposal.ExpiresUtc);
        Assert.Equal(PackageInstallReviewState.Pending, session.State);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Approval_RequiresExplicitUncheckedConsentAndNeverPermitsExecution()
    {
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            new FixtureTrustVerifier(),
            Now);

        await AssertCodeAsync(
            "package_review_approval_required",
            () => session.ApproveForValidationAsync(false, Now.AddMinutes(1)));
        Assert.Equal(PackageInstallReviewState.Pending, session.State);

        var receipt = await session.ApproveForValidationAsync(true, Now.AddMinutes(1));

        Assert.Equal(session.Proposal.ProposalSha256, receipt.ProposalSha256);
        Assert.False(receipt.ExecutionPermitted);
        Assert.Equal(0, receipt.InstallerProcessesStarted);
        Assert.Equal(PackageInstallReviewState.ApprovedForValidation, session.State);
    }

    [Fact]
    public async Task Approval_RehashesAndReverifiesExactPackage()
    {
        var verifier = new FixtureTrustVerifier();
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            verifier,
            Now);

        _ = await session.ApproveForValidationAsync(true, Now.AddMinutes(1));

        Assert.Equal(2, verifier.CallCount);
    }

    [Fact]
    public async Task Approval_DeniesChangedTrustEvidence()
    {
        var verifier = new FixtureTrustVerifier
        {
            EvidenceAfterFirstCall = new(
                true,
                "DCx.AgenticBrowser",
                "CN=Different Publisher",
                "1.2.3.4"),
        };
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            verifier,
            Now);

        await AssertCodeAsync(
            "package_review_package_changed",
            () => session.ApproveForValidationAsync(true, Now.AddMinutes(1)));

        Assert.Equal(PackageInstallReviewState.Denied, session.State);
    }

    [Fact]
    public async Task Approval_DeniesExpiredProposal()
    {
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            new FixtureTrustVerifier(),
            Now);

        await AssertCodeAsync(
            "package_review_expired",
            () => session.ApproveForValidationAsync(true, Now.AddMinutes(11)));

        Assert.Equal(PackageInstallReviewState.Denied, session.State);
    }

    [Fact]
    public async Task Cancel_PreventsLaterApproval()
    {
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            new FixtureTrustVerifier(),
            Now);

        await session.CancelAsync();

        Assert.Equal(PackageInstallReviewState.Cancelled, session.State);
        await AssertCodeAsync(
            "package_review_state_invalid",
            () => session.ApproveForValidationAsync(true, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task Approval_IsSingleUse()
    {
        await using var session = await PackageInstallReviewService.BeginAsync(
            packagePath,
            new FixtureTrustVerifier(),
            Now);
        _ = await session.ApproveForValidationAsync(true, Now.AddMinutes(1));

        await AssertCodeAsync(
            "package_review_state_invalid",
            () => session.ApproveForValidationAsync(true, Now.AddMinutes(2)));
    }

    [Theory]
    [InlineData("fixture.exe")]
    [InlineData("fixture.msi")]
    [InlineData("fixture.msixbundle")]
    public async Task Begin_DeniesUnsupportedPackageKinds(string fileName)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, "unsupported fixture");

        await AssertCodeAsync(
            "package_review_path_invalid",
            async () => await PackageInstallReviewService.BeginAsync(
                path,
                new FixtureTrustVerifier(),
                Now));
    }

    [Fact]
    public async Task Begin_DeniesUntrustedPackage()
    {
        var verifier = new FixtureTrustVerifier
        {
            FirstEvidence = new(false, "DCx.AgenticBrowser", "CN=DCx Fixture", "1.2.3.4"),
        };

        await AssertCodeAsync(
            "package_review_trust_invalid",
            async () => await PackageInstallReviewService.BeginAsync(packagePath, verifier, Now));
    }

    [Fact]
    public async Task Begin_DeniesPackageThroughReparseDirectory()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"dcx-package-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            var outsidePackage = Path.Combine(outside, "outside.msix");
            File.WriteAllText(outsidePackage, "outside fixture");
            var link = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, outside);

            await AssertCodeAsync(
                "package_review_path_invalid",
                async () => await PackageInstallReviewService.BeginAsync(
                    Path.Combine(link, "outside.msix"),
                    new FixtureTrustVerifier(),
                    Now));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
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

    private static async Task AssertCodeAsync(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<PackageInstallReviewException>(action);
        Assert.Equal(expected, exception.Code);
        Assert.Equal(expected, exception.Message);
    }

    private sealed class FixtureTrustVerifier : IPackageInstallTrustVerifier
    {
        public PackageInstallTrustEvidence FirstEvidence { get; set; } =
            new(true, "DCx.AgenticBrowser", "CN=DCx Fixture", "1.2.3.4");

        public PackageInstallTrustEvidence? EvidenceAfterFirstCall { get; set; }

        public int CallCount { get; private set; }

        public Task<PackageInstallTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Path.GetFullPath(packagePath), packagePath);
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                CallCount > 1 && EvidenceAfterFirstCall is not null
                    ? EvidenceAfterFirstCall
                    : FirstEvidence);
        }
    }
}
