using DCx.Core.Deployment;

namespace DCx.PrerequisiteBootstrapper.Core;

/// <summary>Detection results accepted by the bootstrapper policy. These are evidence only, not probes.</summary>
public enum BootstrapperDetectionStatus
{
    Ready,
    Missing,
    Unsupported,
    Unknown,
    Failed,
}

public enum VendorLaunchKind
{
    SelectedArtifact,
    WindowsSubsystemForLinuxInstall,
}

public sealed record PrerequisiteBootstrapperSession(
    string Id,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record PrerequisiteDetectionEvidence(
    string SessionId,
    string PrerequisiteId,
    BootstrapperDetectionStatus Status,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Per-item consent is deliberately session-, prerequisite-, and terms-digest-bound. The booleans
/// default to false so creating a consent record does not grant either acknowledgement or launch approval.
/// </summary>
public sealed record PrerequisiteLaunchConsent(
    string SessionId,
    string PrerequisiteId,
    string LicenseTextDigestReference,
    bool TermsAcknowledged = false,
    bool LaunchApproved = false,
    bool Declined = false);

/// <summary>
/// Describes an already-selected local installer. This Core policy never locates, downloads,
/// validates, or starts it; it only carries a previously selected exact identity into a plan.
/// </summary>
public sealed record SelectedVendorArtifact(
    string SessionId,
    string PrerequisiteId,
    string Sha256,
    string ExpectedPublisher,
    string ExpectedProduct,
    string Path,
    DateTimeOffset SelectedAtUtc);

/// <summary>
/// Caller intent. Caller arguments and every extra authority request must remain absent.
/// </summary>
public sealed record VendorLaunchRequest(
    string PrerequisiteId,
    VendorLaunchKind Kind,
    SelectedVendorArtifact? SelectedArtifact = null,
    IReadOnlyList<string>? CallerArguments = null,
    bool SilentRequested = false,
    bool AcceptTermsRequested = false,
    bool PackageManagerRequested = false,
    bool PowerShellRequested = false,
    bool RebootRequested = false);

/// <summary>
/// A data-only plan for an outer, separately authorized launcher. It is never process, download,
/// package-manager, shell, elevation, terms-acceptance, or reboot authority.
/// </summary>
public sealed record VendorLaunchPlan(
    bool Permitted,
    string Code,
    string? Executable,
    IReadOnlyList<string> Arguments,
    SelectedVendorArtifact? SelectedArtifact,
    bool ProcessExecutionAuthorized,
    bool DownloadAuthorized,
    bool SilentInstallAuthorized,
    bool AcceptTermsAuthorized,
    bool PackageManagerAuthorized,
    bool PowerShellAuthorized,
    bool ElevationAuthorized,
    bool RebootAuthorized);

/// <summary>
/// Pure, fail-closed policy for a bounded prerequisite bootstrapper session. It consumes no
/// machine state and intentionally does not perform detection, filesystem, registry, network,
/// shell, or process operations.
/// </summary>
public static class PrerequisiteBootstrapperPolicy
{
    public static readonly TimeSpan MaximumSessionLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumDetectionAge = TimeSpan.FromMinutes(2);

    private static readonly IReadOnlyList<string> WslArguments = ["--install", "--no-distribution"];

    public static PrerequisiteLaunchConsent CreateInitialConsent(PrerequisiteBootstrapperSession session, string prerequisiteId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var prerequisite = FindPrerequisite(prerequisiteId);

        return new PrerequisiteLaunchConsent(
            session.Id,
            prerequisite.Id,
            prerequisite.LicenseTextDigestReference);
    }

    public static VendorLaunchPlan BuildVendorLaunchPlan(
        PrerequisiteBootstrapperSession session,
        PrerequisiteDetectionEvidence detection,
        PrerequisiteLaunchConsent? consent,
        VendorLaunchRequest request,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(request);

        if (!IsValidSession(session, nowUtc))
        {
            return Denied("session_not_current");
        }

        var prerequisite = FindPrerequisiteOrNull(request.PrerequisiteId);
        if (prerequisite is null)
        {
            return Denied("prerequisite_not_reviewed");
        }

        if (!IsFreshEvidence(session, prerequisite, detection, nowUtc))
        {
            return Denied("detection_not_fresh_or_item_bound");
        }

        if (detection.Status != BootstrapperDetectionStatus.Missing)
        {
            return Denied(detection.Status switch
            {
                BootstrapperDetectionStatus.Ready => "prerequisite_ready",
                BootstrapperDetectionStatus.Unsupported => "prerequisite_unsupported",
                BootstrapperDetectionStatus.Unknown => "prerequisite_detection_unknown",
                BootstrapperDetectionStatus.Failed => "prerequisite_detection_failed",
                _ => "prerequisite_not_missing",
            });
        }

        if (!HasExactConsent(session, prerequisite, consent))
        {
            return Denied("exact_item_terms_and_launch_consent_required");
        }

        if (consent!.Declined)
        {
            return Denied("prerequisite_launch_declined");
        }

        if (HasDisallowedAuthority(request))
        {
            return Denied("disallowed_launch_authority_requested");
        }

        return prerequisite.Id switch
        {
            "webview2-runtime" or "docker-desktop-windows" => BuildArtifactPlan(session, prerequisite, request, nowUtc),
            "windows-wsl2" => BuildWslPlan(request),
            _ => Denied("prerequisite_not_reviewed"),
        };
    }

    private static VendorLaunchPlan BuildArtifactPlan(
        PrerequisiteBootstrapperSession session,
        PrerequisiteBootstrapPrerequisite prerequisite,
        VendorLaunchRequest request,
        DateTimeOffset nowUtc)
    {
        var artifact = request.SelectedArtifact;
        if (request.Kind != VendorLaunchKind.SelectedArtifact ||
            !IsExactSelectedArtifact(session, prerequisite, artifact, nowUtc))
        {
            return Denied("exact_previously_selected_artifact_required");
        }

        return Permitted(artifact!.Path, Array.Empty<string>(), artifact);
    }

    private static VendorLaunchPlan BuildWslPlan(VendorLaunchRequest request) =>
        request.Kind == VendorLaunchKind.WindowsSubsystemForLinuxInstall && request.SelectedArtifact is null
            ? Permitted("wsl.exe", WslArguments, null)
            : Denied("fixed_wsl_launch_required");

    private static bool IsValidSession(PrerequisiteBootstrapperSession session, DateTimeOffset nowUtc) =>
        !string.IsNullOrWhiteSpace(session.Id) &&
        session.StartedAtUtc <= nowUtc &&
        session.ExpiresAtUtc >= nowUtc &&
        session.ExpiresAtUtc >= session.StartedAtUtc &&
        session.ExpiresAtUtc - session.StartedAtUtc <= MaximumSessionLifetime;

    private static bool IsFreshEvidence(
        PrerequisiteBootstrapperSession session,
        PrerequisiteBootstrapPrerequisite prerequisite,
        PrerequisiteDetectionEvidence evidence,
        DateTimeOffset nowUtc) =>
        string.Equals(evidence.SessionId, session.Id, StringComparison.Ordinal) &&
        string.Equals(evidence.PrerequisiteId, prerequisite.Id, StringComparison.Ordinal) &&
        evidence.ObservedAtUtc >= session.StartedAtUtc &&
        evidence.ObservedAtUtc <= nowUtc &&
        nowUtc - evidence.ObservedAtUtc <= MaximumDetectionAge;

    private static bool HasExactConsent(
        PrerequisiteBootstrapperSession session,
        PrerequisiteBootstrapPrerequisite prerequisite,
        PrerequisiteLaunchConsent? consent) =>
        consent is not null &&
        string.Equals(consent.SessionId, session.Id, StringComparison.Ordinal) &&
        string.Equals(consent.PrerequisiteId, prerequisite.Id, StringComparison.Ordinal) &&
        string.Equals(consent.LicenseTextDigestReference, prerequisite.LicenseTextDigestReference, StringComparison.Ordinal) &&
        consent.TermsAcknowledged &&
        consent.LaunchApproved;

    private static bool HasDisallowedAuthority(VendorLaunchRequest request) =>
        request.SilentRequested ||
        request.AcceptTermsRequested ||
        request.PackageManagerRequested ||
        request.PowerShellRequested ||
        request.RebootRequested ||
        request.CallerArguments is { Count: > 0 };

    private static bool IsExactSelectedArtifact(
        PrerequisiteBootstrapperSession session,
        PrerequisiteBootstrapPrerequisite prerequisite,
        SelectedVendorArtifact? artifact,
        DateTimeOffset nowUtc) =>
        artifact is not null &&
        string.Equals(artifact.SessionId, session.Id, StringComparison.Ordinal) &&
        string.Equals(artifact.PrerequisiteId, prerequisite.Id, StringComparison.Ordinal) &&
        string.Equals(artifact.ExpectedPublisher, prerequisite.Publisher, StringComparison.Ordinal) &&
        string.Equals(artifact.ExpectedProduct, prerequisite.DisplayName, StringComparison.Ordinal) &&
        IsSha256(artifact.Sha256) &&
        IsAbsoluteWindowsPath(artifact.Path) &&
        artifact.SelectedAtUtc >= session.StartedAtUtc &&
        artifact.SelectedAtUtc <= nowUtc;

    private static bool IsSha256(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsAbsoluteWindowsPath(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length >= 4 &&
        char.IsAsciiLetter(value[0]) &&
        value[1] == ':' &&
        value[2] == '\\' &&
        !value.Any(char.IsControl) &&
        !value.Contains("..", StringComparison.Ordinal);

    private static PrerequisiteBootstrapPrerequisite FindPrerequisite(string prerequisiteId) =>
        FindPrerequisiteOrNull(prerequisiteId) ?? throw new ArgumentOutOfRangeException(nameof(prerequisiteId));

    private static PrerequisiteBootstrapPrerequisite? FindPrerequisiteOrNull(string prerequisiteId) =>
        PrerequisiteBootstrapPolicy.RequiredPrerequisites.SingleOrDefault(prerequisite =>
            string.Equals(prerequisite.Id, prerequisiteId, StringComparison.Ordinal));

    private static VendorLaunchPlan Permitted(
        string executable,
        IReadOnlyList<string> arguments,
        SelectedVendorArtifact? artifact) =>
        new(
            Permitted: true,
            Code: "vendor_launch_plan_permitted",
            Executable: executable,
            Arguments: arguments,
            SelectedArtifact: artifact,
            ProcessExecutionAuthorized: false,
            DownloadAuthorized: false,
            SilentInstallAuthorized: false,
            AcceptTermsAuthorized: false,
            PackageManagerAuthorized: false,
            PowerShellAuthorized: false,
            ElevationAuthorized: false,
            RebootAuthorized: false);

    private static VendorLaunchPlan Denied(string code) =>
        new(
            Permitted: false,
            Code: code,
            Executable: null,
            Arguments: Array.Empty<string>(),
            SelectedArtifact: null,
            ProcessExecutionAuthorized: false,
            DownloadAuthorized: false,
            SilentInstallAuthorized: false,
            AcceptTermsAuthorized: false,
            PackageManagerAuthorized: false,
            PowerShellAuthorized: false,
            ElevationAuthorized: false,
            RebootAuthorized: false);
}
