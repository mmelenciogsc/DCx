namespace DCx.Core.Deployment;

public enum PrerequisiteDetectionStatus
{
    Unknown,
    Missing,
    Ready,
    Failed,
}

public enum PrerequisiteBootstrapDecisionState
{
    Missing,
    Ready,
    Declined,
    Pending,
    Blocked,
}

public sealed record PrerequisiteBootstrapPrerequisite(
    string Id,
    string DisplayName,
    Uri OfficialUri,
    string Publisher,
    Uri LicenseUri,
    string LicenseTextDigestReference,
    bool RequiresElevation);

public sealed record PrerequisiteBootstrapItem(
    PrerequisiteBootstrapPrerequisite Prerequisite,
    PrerequisiteDetectionStatus DetectionStatus);

public sealed record PrerequisiteBootstrapConsent(
    string PrerequisiteId,
    bool TermsAcknowledged,
    bool InstallApproved,
    bool Declined);

public sealed record PrerequisiteBootstrapDecision(
    PrerequisiteBootstrapDecisionState State,
    string Code,
    PrerequisiteBootstrapItem Item,
    bool TermsAcknowledged,
    bool InstallApproved,
    string StatusText,
    string HelpText,
    bool DownloadAuthorized,
    bool InstallerExecutionAuthorized,
    bool ElevationAuthorized);

/// <summary>
/// Defines the U02 prerequisite-review boundary. It records a per-item decision only;
/// it cannot download, install, invoke a package manager, or request elevation.
/// </summary>
public static class PrerequisiteBootstrapPolicy
{
    private const string NoAuthorityHelpText =
        "DCx Core cannot download, install, start a package manager, or request elevation.";

    private static readonly PrerequisiteBootstrapPrerequisite[] KnownPrerequisites =
    [
        new(
            "webview2-runtime",
            "Microsoft Edge WebView2 Runtime",
            new Uri("https://developer.microsoft.com/microsoft-edge/webview2/consumer/", UriKind.Absolute),
            "Microsoft Corporation",
            new Uri("https://www.microsoft.com/servicesagreement", UriKind.Absolute),
            "sha256:b5cebd43e6af005b18ea1dc96fa21e4d95978671911b83b5732a4ccb5ae2f565",
            RequiresElevation: false),
        new(
            "docker-desktop-windows",
            "Docker Desktop for Windows",
            new Uri("https://www.docker.com/products/docker-desktop/", UriKind.Absolute),
            "Docker Inc.",
            new Uri("https://www.docker.com/legal/docker-subscription-service-agreement/", UriKind.Absolute),
            "sha256:4c282649a4af12d806b1c1bea74b614b46d517b970f76c67d7917bd31a210751",
            RequiresElevation: true),
        new(
            "windows-wsl2",
            "Windows Subsystem for Linux 2 (WSL 2)",
            new Uri("https://learn.microsoft.com/en-us/windows/wsl/install", UriKind.Absolute),
            "Microsoft Corporation",
            new Uri("https://www.microsoft.com/servicesagreement", UriKind.Absolute),
            "sha256:b5cebd43e6af005b18ea1dc96fa21e4d95978671911b83b5732a4ccb5ae2f565",
            RequiresElevation: true),
    ];

    public static IReadOnlyList<PrerequisiteBootstrapPrerequisite> RequiredPrerequisites { get; } =
        Array.AsReadOnly(KnownPrerequisites);

    public static PrerequisiteBootstrapDecision Evaluate(
        PrerequisiteBootstrapItem item,
        PrerequisiteBootstrapConsent? consent = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Prerequisite);

        if (!IsKnownSafePrerequisite(item.Prerequisite))
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Blocked,
                "prerequisite_definition_unsafe",
                item,
                termsAcknowledged: false,
                installApproved: false,
                "Prerequisite bootstrap is blocked because its reviewed definition is unsafe.",
                NoAuthorityHelpText);
        }

        if (item.DetectionStatus is PrerequisiteDetectionStatus.Unknown or PrerequisiteDetectionStatus.Failed)
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Blocked,
                "prerequisite_detection_unavailable",
                item,
                termsAcknowledged: false,
                installApproved: false,
                $"{item.Prerequisite.DisplayName} bootstrap is blocked until its detection result is available.",
                NoAuthorityHelpText);
        }

        if (item.DetectionStatus == PrerequisiteDetectionStatus.Ready)
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Ready,
                "prerequisite_ready",
                item,
                termsAcknowledged: false,
                installApproved: false,
                $"{item.Prerequisite.DisplayName} is ready. No bootstrap action is required.",
                NoAuthorityHelpText);
        }

        if (consent?.Declined == true && IsConsentFor(item, consent))
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Declined,
                "prerequisite_install_declined",
                item,
                termsAcknowledged: false,
                installApproved: false,
                $"Installation of {item.Prerequisite.DisplayName} was declined.",
                NoAuthorityHelpText);
        }

        if (consent is not null && !IsConsentFor(item, consent))
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Pending,
                "prerequisite_consent_item_mismatch",
                item,
                termsAcknowledged: false,
                installApproved: false,
                $"{item.Prerequisite.DisplayName} is missing. Its consent must be recorded separately.",
                NoAuthorityHelpText);
        }

        var termsAcknowledged = consent?.TermsAcknowledged == true;
        if (!termsAcknowledged)
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Pending,
                "prerequisite_terms_acknowledgement_required",
                item,
                termsAcknowledged: false,
                installApproved: false,
                $"{item.Prerequisite.DisplayName} is missing. A separate acknowledgement of its terms is required.",
                NoAuthorityHelpText);
        }

        var installApproved = consent?.InstallApproved == true;
        if (!installApproved)
        {
            return Decision(
                PrerequisiteBootstrapDecisionState.Pending,
                "prerequisite_install_approval_required",
                item,
                termsAcknowledged: true,
                installApproved: false,
                $"{item.Prerequisite.DisplayName} is missing. A separate installation approval is required.",
                NoAuthorityHelpText);
        }

        return Decision(
            PrerequisiteBootstrapDecisionState.Missing,
            "prerequisite_missing_external_install_required",
            item,
            termsAcknowledged: true,
            installApproved: true,
            $"{item.Prerequisite.DisplayName} is still missing. Consent is recorded for this item only.",
            NoAuthorityHelpText);
    }

    private static PrerequisiteBootstrapDecision Decision(
        PrerequisiteBootstrapDecisionState state,
        string code,
        PrerequisiteBootstrapItem item,
        bool termsAcknowledged,
        bool installApproved,
        string statusText,
        string helpText) =>
        new(
            state,
            code,
            item,
            termsAcknowledged,
            installApproved,
            statusText,
            helpText,
            DownloadAuthorized: false,
            InstallerExecutionAuthorized: false,
            ElevationAuthorized: false);

    private static bool IsConsentFor(
        PrerequisiteBootstrapItem item,
        PrerequisiteBootstrapConsent consent) =>
        string.Equals(item.Prerequisite.Id, consent.PrerequisiteId, StringComparison.Ordinal);

    private static bool IsKnownSafePrerequisite(PrerequisiteBootstrapPrerequisite prerequisite) =>
        KnownPrerequisites.Any(known => known == prerequisite) &&
        IsSafeOfficialUri(prerequisite.OfficialUri) &&
        IsSafeOfficialUri(prerequisite.LicenseUri) &&
        IsSha256Reference(prerequisite.LicenseTextDigestReference);

    private static bool IsSafeOfficialUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsSha256Reference(string? value) =>
        value is { Length: 71 } && value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
