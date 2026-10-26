using DCx.Core.Deployment;

namespace DCx.Core.Tests.Deployment;

public sealed class PrerequisiteBootstrapPolicyTests
{
    [Fact]
    public void Catalog_UsesExactReviewedPrerequisiteMetadata()
    {
        var prerequisites = PrerequisiteBootstrapPolicy.RequiredPrerequisites;

        Assert.Collection(
            prerequisites,
            webView2 =>
            {
                Assert.Equal("webview2-runtime", webView2.Id);
                Assert.Equal("Microsoft Edge WebView2 Runtime", webView2.DisplayName);
                Assert.Equal("https://developer.microsoft.com/microsoft-edge/webview2/consumer/", webView2.OfficialUri.AbsoluteUri);
                Assert.Equal("Microsoft Corporation", webView2.Publisher);
                Assert.False(webView2.RequiresElevation);
                Assert.Equal("https://www.microsoft.com/servicesagreement", webView2.LicenseUri.AbsoluteUri);
                Assert.Equal("sha256:b5cebd43e6af005b18ea1dc96fa21e4d95978671911b83b5732a4ccb5ae2f565", webView2.LicenseTextDigestReference);
            },
            docker =>
            {
                Assert.Equal("docker-desktop-windows", docker.Id);
                Assert.Equal("Docker Desktop for Windows", docker.DisplayName);
                Assert.Equal("https://www.docker.com/products/docker-desktop/", docker.OfficialUri.AbsoluteUri);
                Assert.Equal("Docker Inc.", docker.Publisher);
                Assert.True(docker.RequiresElevation);
                Assert.Equal("https://www.docker.com/legal/docker-subscription-service-agreement/", docker.LicenseUri.AbsoluteUri);
                Assert.Equal("sha256:4c282649a4af12d806b1c1bea74b614b46d517b970f76c67d7917bd31a210751", docker.LicenseTextDigestReference);
            },
            wsl2 =>
            {
                Assert.Equal("windows-wsl2", wsl2.Id);
                Assert.Equal("Windows Subsystem for Linux 2 (WSL 2)", wsl2.DisplayName);
                Assert.Equal("https://learn.microsoft.com/en-us/windows/wsl/install", wsl2.OfficialUri.AbsoluteUri);
                Assert.Equal("Microsoft Corporation", wsl2.Publisher);
                Assert.True(wsl2.RequiresElevation);
                Assert.Equal("https://www.microsoft.com/servicesagreement", wsl2.LicenseUri.AbsoluteUri);
                Assert.Equal("sha256:b5cebd43e6af005b18ea1dc96fa21e4d95978671911b83b5732a4ccb5ae2f565", wsl2.LicenseTextDigestReference);
            });
    }

    [Fact]
    public void Catalog_HasNoLinuxTargetPrerequisite()
    {
        var prerequisites = PrerequisiteBootstrapPolicy.RequiredPrerequisites;

        Assert.DoesNotContain(prerequisites, prerequisite =>
            prerequisite.Id is "dotnet-sdk-10.0.401-linux-x64" or "docker-engine" ||
            prerequisite.OfficialUri.IdnHost is "builds.dotnet.microsoft.com" or "docs.docker.com");
    }

    [Fact]
    public void MissingPrerequisite_DefaultsToPendingAndDeniesAllAuthority()
    {
        var decision = Evaluate(PrerequisiteBootstrapPolicy.RequiredPrerequisites[0]);

        Assert.Equal(PrerequisiteBootstrapDecisionState.Pending, decision.State);
        Assert.Equal("prerequisite_terms_acknowledgement_required", decision.Code);
        Assert.False(decision.TermsAcknowledged);
        Assert.False(decision.InstallApproved);
        Assert.Contains("separate acknowledgement", decision.StatusText, StringComparison.Ordinal);
        Assert.Contains("cannot download, install", decision.HelpText, StringComparison.Ordinal);
        AssertNoInstallAuthority(decision);
    }

    [Fact]
    public void Consent_IsBoundToItsExactPrerequisiteAndDoesNotTransfer()
    {
        var webView2 = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var docker = PrerequisiteBootstrapPolicy.RequiredPrerequisites[1];
        var webView2Consent = new PrerequisiteBootstrapConsent(
            webView2.Id,
            TermsAcknowledged: true,
            InstallApproved: true,
            Declined: false);

        var webView2Decision = Evaluate(webView2, webView2Consent);
        var dockerDecision = Evaluate(docker, webView2Consent);

        Assert.Equal(PrerequisiteBootstrapDecisionState.Missing, webView2Decision.State);
        Assert.True(webView2Decision.TermsAcknowledged);
        Assert.True(webView2Decision.InstallApproved);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Pending, dockerDecision.State);
        Assert.Equal("prerequisite_consent_item_mismatch", dockerDecision.Code);
        Assert.False(dockerDecision.TermsAcknowledged);
        Assert.False(dockerDecision.InstallApproved);
        AssertNoInstallAuthority(webView2Decision);
        AssertNoInstallAuthority(dockerDecision);
    }

    [Fact]
    public void MissingPrerequisite_RequiresSeparateTermsAndInstallApproval()
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var termsOnly = Evaluate(prerequisite, new(
            prerequisite.Id,
            TermsAcknowledged: true,
            InstallApproved: false,
            Declined: false));
        var approvalOnly = Evaluate(prerequisite, new(
            prerequisite.Id,
            TermsAcknowledged: false,
            InstallApproved: true,
            Declined: false));
        var both = Evaluate(prerequisite, new(
            prerequisite.Id,
            TermsAcknowledged: true,
            InstallApproved: true,
            Declined: false));

        Assert.Equal(PrerequisiteBootstrapDecisionState.Pending, termsOnly.State);
        Assert.Equal("prerequisite_install_approval_required", termsOnly.Code);
        Assert.True(termsOnly.TermsAcknowledged);
        Assert.False(termsOnly.InstallApproved);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Pending, approvalOnly.State);
        Assert.Equal("prerequisite_terms_acknowledgement_required", approvalOnly.Code);
        Assert.False(approvalOnly.TermsAcknowledged);
        Assert.False(approvalOnly.InstallApproved);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Missing, both.State);
        Assert.True(both.TermsAcknowledged);
        Assert.True(both.InstallApproved);
    }

    [Fact]
    public void DecisionStates_IncludeReadyDeclinedAndBlocked()
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var ready = PrerequisiteBootstrapPolicy.Evaluate(new(prerequisite, PrerequisiteDetectionStatus.Ready));
        var declined = Evaluate(prerequisite, new(
            prerequisite.Id,
            TermsAcknowledged: false,
            InstallApproved: false,
            Declined: true));
        var blocked = PrerequisiteBootstrapPolicy.Evaluate(new(prerequisite, PrerequisiteDetectionStatus.Failed));

        Assert.Equal(PrerequisiteBootstrapDecisionState.Ready, ready.State);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Declined, declined.State);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Blocked, blocked.State);
        Assert.All([ready, declined, blocked], AssertNoInstallAuthority);
    }

    [Fact]
    public void UnsafeOfficialOrLicenseUri_IsBlocked()
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var unsafeOfficial = prerequisite with
        {
            OfficialUri = new Uri("http://developer.microsoft.com/microsoft-edge/webview2/consumer/", UriKind.Absolute),
        };
        var unsafeLicense = prerequisite with
        {
            LicenseUri = new Uri("https://www.microsoft.com/servicesagreement?unreviewed=true", UriKind.Absolute),
        };

        var officialDecision = Evaluate(unsafeOfficial);
        var licenseDecision = Evaluate(unsafeLicense);

        Assert.Equal(PrerequisiteBootstrapDecisionState.Blocked, officialDecision.State);
        Assert.Equal("prerequisite_definition_unsafe", officialDecision.Code);
        Assert.Equal(PrerequisiteBootstrapDecisionState.Blocked, licenseDecision.State);
        Assert.Equal("prerequisite_definition_unsafe", licenseDecision.Code);
        AssertNoInstallAuthority(officialDecision);
        AssertNoInstallAuthority(licenseDecision);
    }

    [Fact]
    public void ExternalResourceHandoff_PermitsOnlyTheExactCatalogOfficialResourceAfterFullConsent()
    {
        foreach (var prerequisite in PrerequisiteBootstrapPolicy.RequiredPrerequisites)
        {
            var item = new PrerequisiteBootstrapItem(prerequisite, PrerequisiteDetectionStatus.Missing);
            var consent = new PrerequisiteBootstrapConsent(
                prerequisite.Id,
                TermsAcknowledged: true,
                InstallApproved: true,
                Declined: false);

            var decision = PrerequisiteBootstrapPolicy.EvaluateExternalResourceHandoff(item, consent);

            Assert.True(decision.Permitted);
            Assert.Equal("official_resource_handoff_permitted", decision.Code);
            Assert.Same(item, decision.Item);
            Assert.Equal(prerequisite.OfficialUri, decision.OfficialResourceUri);
            Assert.Equal(prerequisite.OfficialUri.AbsoluteUri, decision.OfficialResourceUri!.AbsoluteUri);
            AssertNoInstallAuthority(decision);
        }
    }

    [Theory]
    [InlineData(PrerequisiteDetectionStatus.Unknown, "prerequisite_detection_unavailable")]
    [InlineData(PrerequisiteDetectionStatus.Failed, "prerequisite_detection_unavailable")]
    [InlineData(PrerequisiteDetectionStatus.Ready, "prerequisite_ready")]
    public void ExternalResourceHandoff_RequiresAConfirmedMissingDetection(
        PrerequisiteDetectionStatus detectionStatus,
        string expectedCode)
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var decision = PrerequisiteBootstrapPolicy.EvaluateExternalResourceHandoff(
            new PrerequisiteBootstrapItem(prerequisite, detectionStatus),
            new PrerequisiteBootstrapConsent(
                prerequisite.Id,
                TermsAcknowledged: true,
                InstallApproved: true,
                Declined: false));

        Assert.False(decision.Permitted);
        Assert.Equal(expectedCode, decision.Code);
        Assert.Null(decision.OfficialResourceUri);
        AssertNoInstallAuthority(decision);
    }

    [Fact]
    public void ExternalResourceHandoff_RequiresExactItemTermsAndInstallConsentWithoutDecline()
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var otherPrerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[1];
        var item = new PrerequisiteBootstrapItem(prerequisite, PrerequisiteDetectionStatus.Missing);
        var deniedCases = new (PrerequisiteBootstrapConsent? Consent, string Code)[]
        {
            (null, "prerequisite_terms_acknowledgement_required"),
            (new PrerequisiteBootstrapConsent(otherPrerequisite.Id, true, true, false), "prerequisite_consent_item_mismatch"),
            (new PrerequisiteBootstrapConsent(prerequisite.Id, false, true, false), "prerequisite_terms_acknowledgement_required"),
            (new PrerequisiteBootstrapConsent(prerequisite.Id, true, false, false), "prerequisite_install_approval_required"),
            (new PrerequisiteBootstrapConsent(prerequisite.Id, true, true, true), "prerequisite_install_declined"),
        };

        foreach (var (consent, expectedCode) in deniedCases)
        {
            var decision = PrerequisiteBootstrapPolicy.EvaluateExternalResourceHandoff(item, consent);

            Assert.False(decision.Permitted);
            Assert.Equal(expectedCode, decision.Code);
            Assert.Null(decision.OfficialResourceUri);
            AssertNoInstallAuthority(decision);
        }
    }

    [Fact]
    public void ExternalResourceHandoff_RejectsAnUnsafeOrUnreviewedDefinition()
    {
        var prerequisite = PrerequisiteBootstrapPolicy.RequiredPrerequisites[0];
        var alteredDefinition = prerequisite with
        {
            OfficialUri = new Uri("https://developer.microsoft.com/microsoft-edge/webview2/consumer/?redirect=unreviewed", UriKind.Absolute),
        };
        var decision = PrerequisiteBootstrapPolicy.EvaluateExternalResourceHandoff(
            new PrerequisiteBootstrapItem(alteredDefinition, PrerequisiteDetectionStatus.Missing),
            new PrerequisiteBootstrapConsent(
                alteredDefinition.Id,
                TermsAcknowledged: true,
                InstallApproved: true,
                Declined: false));

        Assert.False(decision.Permitted);
        Assert.Equal("prerequisite_definition_unsafe", decision.Code);
        Assert.Null(decision.OfficialResourceUri);
        AssertNoInstallAuthority(decision);
    }

    private static PrerequisiteBootstrapDecision Evaluate(
        PrerequisiteBootstrapPrerequisite prerequisite,
        PrerequisiteBootstrapConsent? consent = null) =>
        PrerequisiteBootstrapPolicy.Evaluate(
            new PrerequisiteBootstrapItem(prerequisite, PrerequisiteDetectionStatus.Missing),
            consent);

    private static void AssertNoInstallAuthority(PrerequisiteBootstrapDecision decision)
    {
        Assert.False(decision.DownloadAuthorized);
        Assert.False(decision.InstallerExecutionAuthorized);
        Assert.False(decision.ElevationAuthorized);
    }

    private static void AssertNoInstallAuthority(PrerequisiteExternalResourceHandoffDecision decision)
    {
        Assert.False(decision.DownloadAuthorized);
        Assert.False(decision.InstallerExecutionAuthorized);
        Assert.False(decision.PackageManagerAuthorized);
        Assert.False(decision.ElevationAuthorized);
    }
}
