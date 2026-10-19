namespace DCx.Core.Deployment;

public sealed record WebView2PrerequisiteHandoffDecision(
    bool Permitted,
    string Code,
    Uri OfficialConsumerPage,
    bool DownloadAuthorized,
    bool InstallerExecutionAuthorized,
    bool ElevationAuthorized);

public static class WebView2PrerequisiteHandoffPolicy
{
    public const string OfficialConsumerPageAddress =
        "https://developer.microsoft.com/microsoft-edge/webview2/consumer/";

    public static Uri OfficialConsumerPage { get; } = new(OfficialConsumerPageAddress, UriKind.Absolute);

    public static WebView2PrerequisiteHandoffDecision Evaluate(
        bool runtimeVerifiedMissing,
        bool exactHandoffApproved)
    {
        var code = !runtimeVerifiedMissing
            ? "runtime_missing_not_verified"
            : !exactHandoffApproved
                ? "handoff_approval_required"
                : "official_page_handoff_permitted";

        return new WebView2PrerequisiteHandoffDecision(
            Permitted: runtimeVerifiedMissing && exactHandoffApproved,
            Code: code,
            OfficialConsumerPage,
            DownloadAuthorized: false,
            InstallerExecutionAuthorized: false,
            ElevationAuthorized: false);
    }
}
