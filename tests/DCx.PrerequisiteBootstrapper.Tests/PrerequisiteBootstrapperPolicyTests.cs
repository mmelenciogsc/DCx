using DCx.Core.Deployment;
using DCx.PrerequisiteBootstrapper.Core;
using CorePrerequisiteBootstrapPolicy = DCx.Core.Deployment.PrerequisiteBootstrapPolicy;
using Xunit;

namespace DCx.PrerequisiteBootstrapper.Tests;

public sealed class PrerequisiteBootstrapperPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrerequisiteBootstrapperSession Session = new("session-1", Now.AddMinutes(-1), Now.AddMinutes(4));

    [Fact]
    public void Catalog_IsReusedWithoutChangingCorePolicy()
    {
        var initial = PrerequisiteBootstrapperPolicy.CreateInitialConsent(Session, "webview2-runtime");

        Assert.Equal(CorePrerequisiteBootstrapPolicy.RequiredPrerequisites[0].LicenseTextDigestReference, initial.LicenseTextDigestReference);
        Assert.False(initial.TermsAcknowledged);
        Assert.False(initial.LaunchApproved);
        Assert.False(initial.Declined);
    }

    [Theory]
    [InlineData("webview2-runtime")]
    [InlineData("docker-desktop-windows")]
    public void MissingWithExactConsentAndSelectedArtifact_ProducesDataOnlyPlan(string prerequisiteId)
    {
        var plan = Build(prerequisiteId, VendorLaunchKind.SelectedArtifact, SelectedArtifact(prerequisiteId));

        Assert.True(plan.Permitted);
        Assert.Equal("vendor_launch_plan_permitted", plan.Code);
        Assert.Equal(SelectedArtifact(prerequisiteId).Path, plan.Executable);
        Assert.Empty(plan.Arguments);
        Assert.Equal(prerequisiteId, plan.SelectedArtifact!.PrerequisiteId);
        AssertNoAuthority(plan);
    }

    [Fact]
    public void MissingWslWithExactConsent_ProducesOnlyFixedWslInvocation()
    {
        var plan = Build("windows-wsl2", VendorLaunchKind.WindowsSubsystemForLinuxInstall);

        Assert.True(plan.Permitted);
        Assert.Equal("wsl.exe", plan.Executable);
        Assert.Equal(["--install", "--no-distribution"], plan.Arguments);
        Assert.Null(plan.SelectedArtifact);
        AssertNoAuthority(plan);
    }

    [Theory]
    [InlineData(BootstrapperDetectionStatus.Ready, "prerequisite_ready")]
    [InlineData(BootstrapperDetectionStatus.Unsupported, "prerequisite_unsupported")]
    [InlineData(BootstrapperDetectionStatus.Unknown, "prerequisite_detection_unknown")]
    [InlineData(BootstrapperDetectionStatus.Failed, "prerequisite_detection_failed")]
    public void NonMissingDetection_IsDenied(BootstrapperDetectionStatus status, string code)
    {
        var plan = Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, SelectedArtifact("webview2-runtime"), status: status);

        AssertDenied(plan, code);
    }

    [Fact]
    public void StaleOrMismatchedEvidence_IsDenied()
    {
        var stale = Evidence("webview2-runtime") with { ObservedAtUtc = Now.AddMinutes(-3) };
        var mismatched = Evidence("docker-desktop-windows");

        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, SelectedArtifact("webview2-runtime"), evidence: stale), "detection_not_fresh_or_item_bound");
        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, SelectedArtifact("webview2-runtime"), evidence: mismatched), "detection_not_fresh_or_item_bound");
    }

    [Fact]
    public void MismatchedOrDeclinedConsent_IsDenied()
    {
        var mismatched = Consent("docker-desktop-windows");
        var declined = Consent("webview2-runtime") with { Declined = true };

        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, SelectedArtifact("webview2-runtime"), consent: mismatched), "exact_item_terms_and_launch_consent_required");
        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, SelectedArtifact("webview2-runtime"), consent: declined), "prerequisite_launch_declined");
    }

    [Fact]
    public void ArtifactIdentityMustBeExactAndPreviouslySelected()
    {
        var invalidHash = SelectedArtifact("webview2-runtime") with { Sha256 = "not-a-hash" };
        var alteredPublisher = SelectedArtifact("webview2-runtime") with { ExpectedPublisher = "Other" };
        var futureSelection = SelectedArtifact("webview2-runtime") with { SelectedAtUtc = Now.AddMinutes(1) };

        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, invalidHash), "exact_previously_selected_artifact_required");
        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, alteredPublisher), "exact_previously_selected_artifact_required");
        AssertDenied(Build("webview2-runtime", VendorLaunchKind.SelectedArtifact, futureSelection), "exact_previously_selected_artifact_required");
    }

    [Fact]
    public void HostileAuthorityAndCallerArguments_AreDenied()
    {
        var request = new VendorLaunchRequest(
            "windows-wsl2",
            VendorLaunchKind.WindowsSubsystemForLinuxInstall,
            CallerArguments: ["--extra"],
            SilentRequested: true,
            AcceptTermsRequested: true,
            PackageManagerRequested: true,
            PowerShellRequested: true,
            RebootRequested: true);

        var plan = PrerequisiteBootstrapperPolicy.BuildVendorLaunchPlan(
            Session,
            Evidence("windows-wsl2"),
            Consent("windows-wsl2"),
            request,
            Now);

        AssertDenied(plan, "disallowed_launch_authority_requested");
    }

    [Fact]
    public void WslRejectsArtifactsAndAnyNonFixedKind()
    {
        var withArtifact = Build("windows-wsl2", VendorLaunchKind.WindowsSubsystemForLinuxInstall, SelectedArtifact("windows-wsl2"));
        var wrongKind = Build("windows-wsl2", VendorLaunchKind.SelectedArtifact);

        AssertDenied(withArtifact, "fixed_wsl_launch_required");
        AssertDenied(wrongKind, "fixed_wsl_launch_required");
    }

    private static VendorLaunchPlan Build(
        string prerequisiteId,
        VendorLaunchKind kind,
        SelectedVendorArtifact? artifact = null,
        BootstrapperDetectionStatus status = BootstrapperDetectionStatus.Missing,
        PrerequisiteDetectionEvidence? evidence = null,
        PrerequisiteLaunchConsent? consent = null) =>
        PrerequisiteBootstrapperPolicy.BuildVendorLaunchPlan(
            Session,
            evidence ?? Evidence(prerequisiteId, status),
            consent ?? Consent(prerequisiteId),
            new VendorLaunchRequest(prerequisiteId, kind, artifact),
            Now);

    private static PrerequisiteDetectionEvidence Evidence(
        string prerequisiteId,
        BootstrapperDetectionStatus status = BootstrapperDetectionStatus.Missing) =>
        new(Session.Id, prerequisiteId, status, Now.AddSeconds(-30));

    private static PrerequisiteLaunchConsent Consent(string prerequisiteId) =>
        PrerequisiteBootstrapperPolicy.CreateInitialConsent(Session, prerequisiteId) with
        {
            TermsAcknowledged = true,
            LaunchApproved = true,
        };

    private static SelectedVendorArtifact SelectedArtifact(string prerequisiteId)
    {
        var prerequisite = CorePrerequisiteBootstrapPolicy.RequiredPrerequisites.Single(item => item.Id == prerequisiteId);
        return new SelectedVendorArtifact(
            Session.Id,
            prerequisiteId,
            new string('a', 64),
            prerequisite.Publisher,
            prerequisite.DisplayName,
            "C:\\Installers\\reviewed-installer.exe",
            Now.AddSeconds(-15));
    }

    private static void AssertDenied(VendorLaunchPlan plan, string code)
    {
        Assert.False(plan.Permitted);
        Assert.Equal(code, plan.Code);
        Assert.Null(plan.Executable);
        Assert.Empty(plan.Arguments);
        Assert.Null(plan.SelectedArtifact);
        AssertNoAuthority(plan);
    }

    private static void AssertNoAuthority(VendorLaunchPlan plan)
    {
        Assert.False(plan.ProcessExecutionAuthorized);
        Assert.False(plan.DownloadAuthorized);
        Assert.False(plan.SilentInstallAuthorized);
        Assert.False(plan.AcceptTermsAuthorized);
        Assert.False(plan.PackageManagerAuthorized);
        Assert.False(plan.PowerShellAuthorized);
        Assert.False(plan.ElevationAuthorized);
        Assert.False(plan.RebootAuthorized);
    }
}
