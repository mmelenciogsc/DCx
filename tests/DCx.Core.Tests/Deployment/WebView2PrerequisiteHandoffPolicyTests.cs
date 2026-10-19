using DCx.Core.Deployment;

namespace DCx.Core.Tests.Deployment;

public sealed class WebView2PrerequisiteHandoffPolicyTests
{
    [Fact]
    public void OfficialConsumerPage_IsExactHttpsMicrosoftAddress()
    {
        var page = WebView2PrerequisiteHandoffPolicy.OfficialConsumerPage;

        Assert.Equal("https", page.Scheme);
        Assert.Equal("developer.microsoft.com", page.IdnHost);
        Assert.Equal("/microsoft-edge/webview2/consumer/", page.AbsolutePath);
        Assert.Empty(page.Query);
        Assert.Empty(page.Fragment);
        Assert.Equal(WebView2PrerequisiteHandoffPolicy.OfficialConsumerPageAddress, page.AbsoluteUri);
    }

    [Fact]
    public void Handoff_RequiresFreshMissingRuntimeEvidenceAndExactApproval()
    {
        var present = WebView2PrerequisiteHandoffPolicy.Evaluate(
            runtimeVerifiedMissing: false,
            exactHandoffApproved: true);
        var uncheckedApproval = WebView2PrerequisiteHandoffPolicy.Evaluate(
            runtimeVerifiedMissing: true,
            exactHandoffApproved: false);
        var approved = WebView2PrerequisiteHandoffPolicy.Evaluate(
            runtimeVerifiedMissing: true,
            exactHandoffApproved: true);

        Assert.False(present.Permitted);
        Assert.Equal("runtime_missing_not_verified", present.Code);
        Assert.False(uncheckedApproval.Permitted);
        Assert.Equal("handoff_approval_required", uncheckedApproval.Code);
        Assert.True(approved.Permitted);
        Assert.Equal("official_page_handoff_permitted", approved.Code);
    }

    [Fact]
    public void PermittedHandoff_GrantsNoDownloadInstallerOrElevationAuthority()
    {
        var decision = WebView2PrerequisiteHandoffPolicy.Evaluate(
            runtimeVerifiedMissing: true,
            exactHandoffApproved: true);

        Assert.False(decision.DownloadAuthorized);
        Assert.False(decision.InstallerExecutionAuthorized);
        Assert.False(decision.ElevationAuthorized);
    }
}
