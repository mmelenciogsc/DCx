namespace DCx.Core.Reliability;

public sealed record ControlledUpdateSourceSnapshot(
    string StatusCode,
    string CurrentVersion,
    DateTimeOffset GeneratedUtc,
    ControlledUpdateReview? Review,
    int ManifestRequests,
    int PackageRequests,
    int NetworkRequests);

public sealed record ControlledUpdateStageResult(
    string ReviewSha256,
    string Version,
    string PackageSha256,
    long PackageLength,
    int ManifestRequests,
    int PackageRequests,
    int NetworkRequests,
    bool ExecutionPermitted,
    int InstallerProcessesStarted,
    int ElevationRequests,
    int ActivationAttempts,
    int RestartAttempts,
    int ProductionActions);

public sealed record ControlledUpdateActivationReadinessResult(
    ControlledUpdateActivationReadiness Readiness,
    int ManifestRequests,
    int PackageRequests,
    int NetworkRequests,
    int InstallerProcessesStarted,
    int ElevationRequests,
    int ActivationAttempts,
    int RestartAttempts,
    int ProductionActions);

public interface IControlledUpdateReviewSource
{
    Task<ControlledUpdateSourceSnapshot> CheckAsync(
        CancellationToken cancellationToken = default);

    Task<ControlledUpdateStageResult> StageAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default);

    Task<ControlledUpdateActivationReadinessResult> PrepareActivationReadinessAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default);
}

public static class ControlledUpdateReviewSourcePolicy
{
    public const string NotConfigured = "not_configured";
    public const string Available = "available";
    public const int MaximumRequestCount = 10;

    public static void RequireSnapshot(ControlledUpdateSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = ControlledUpdateManifestCodec.ParsePackageVersion(snapshot.CurrentVersion);
        if (snapshot.StatusCode is not (NotConfigured or Available) ||
            snapshot.GeneratedUtc == default || snapshot.GeneratedUtc.Offset != TimeSpan.Zero ||
            snapshot.ManifestRequests is < 0 or > MaximumRequestCount ||
            snapshot.PackageRequests is < 0 or > MaximumRequestCount ||
            snapshot.NetworkRequests is < 0 or > MaximumRequestCount ||
            snapshot.NetworkRequests > snapshot.ManifestRequests + snapshot.PackageRequests)
        {
            throw new ControlledUpdateException("update_source_snapshot_invalid");
        }

        if (snapshot.StatusCode == NotConfigured)
        {
            if (snapshot.Review is not null || snapshot.ManifestRequests != 0 ||
                snapshot.PackageRequests != 0 || snapshot.NetworkRequests != 0)
            {
                throw new ControlledUpdateException("update_source_snapshot_invalid");
            }

            return;
        }

        if (snapshot.Review is null || snapshot.ManifestRequests < 1 ||
            snapshot.PackageRequests != 0 ||
            snapshot.GeneratedUtc < snapshot.Review.PublishedUtc.AddMinutes(-5) ||
            snapshot.GeneratedUtc >= snapshot.Review.ExpiresUtc ||
            !string.Equals(
                snapshot.CurrentVersion,
                snapshot.Review.CurrentVersion,
                StringComparison.Ordinal))
        {
            throw new ControlledUpdateException("update_source_snapshot_invalid");
        }

        ControlledUpdateReviewPolicy.Require(snapshot.Review);
    }

    public static void RequireResult(
        ControlledUpdateReview review,
        ControlledUpdateStageResult result)
    {
        ControlledUpdateReviewPolicy.Require(review);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.ReviewSha256, review.ReviewSha256, StringComparison.Ordinal) ||
            !string.Equals(result.Version, review.CandidateVersion, StringComparison.Ordinal) ||
            !string.Equals(result.PackageSha256, review.PackageSha256, StringComparison.Ordinal) ||
            result.PackageLength != review.PackageLength ||
            result.ManifestRequests is < 2 or > MaximumRequestCount ||
            result.PackageRequests is < 1 or > MaximumRequestCount ||
            result.NetworkRequests is < 0 or > MaximumRequestCount ||
            result.NetworkRequests > result.ManifestRequests + result.PackageRequests ||
            result.ExecutionPermitted || result.InstallerProcessesStarted != 0 ||
            result.ElevationRequests != 0 || result.ActivationAttempts != 0 ||
            result.RestartAttempts != 0 || result.ProductionActions != 0)
        {
            throw new ControlledUpdateException("update_stage_result_invalid");
        }
    }

    public static void RequireActivationReadinessResult(
        ControlledUpdateReview review,
        ControlledUpdateActivationReadinessResult result)
    {
        ControlledUpdateReviewPolicy.Require(review);
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            ControlledUpdateActivationReadinessPolicy.Require(review, result.Readiness);
        }
        catch (ControlledUpdateException)
        {
            throw new ControlledUpdateException("update_activation_readiness_result_invalid");
        }

        if (result.ManifestRequests is < 2 or > MaximumRequestCount ||
            result.PackageRequests is < 1 or > MaximumRequestCount ||
            result.NetworkRequests is < 0 or > MaximumRequestCount ||
            result.NetworkRequests > result.ManifestRequests + result.PackageRequests ||
            result.InstallerProcessesStarted != 0 || result.ElevationRequests != 0 ||
            result.ActivationAttempts != 0 || result.RestartAttempts != 0 ||
            result.ProductionActions != 0)
        {
            throw new ControlledUpdateException("update_activation_readiness_result_invalid");
        }
    }
}

public sealed class UnavailableControlledUpdateReviewSource(string currentVersion)
    : IControlledUpdateReviewSource
{
    private readonly string currentVersion =
        ControlledUpdateManifestCodec.ParsePackageVersion(currentVersion).ToString(4);

    public Task<ControlledUpdateSourceSnapshot> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new ControlledUpdateSourceSnapshot(
            ControlledUpdateReviewSourcePolicy.NotConfigured,
            currentVersion,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0);
        ControlledUpdateReviewSourcePolicy.RequireSnapshot(snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<ControlledUpdateStageResult> StageAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new ControlledUpdateException("update_source_unavailable");
    }

    public Task<ControlledUpdateActivationReadinessResult> PrepareActivationReadinessAsync(
        ControlledUpdateReview review,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new ControlledUpdateException("update_source_unavailable");
    }
}
