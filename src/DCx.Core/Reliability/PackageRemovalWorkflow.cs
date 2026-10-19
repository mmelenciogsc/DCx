using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.Reliability;

public enum PackageRemovalSourceKind
{
    Unavailable,
    ApprovedCurrentPackage,
    GeneratedFixture,
}

public enum PackageRemovalAvailability
{
    NotPackaged,
    ProductionIdentityNotConfigured,
    Available,
}

public sealed record PackageRemovalReview(
    string PackageName,
    string PackageFullName,
    string Publisher,
    string PublisherId,
    string Version,
    string Architecture,
    DateTimeOffset ReviewedUtc,
    DateTimeOffset ExpiresUtc,
    string ReviewSha256);

public static class PackageRemovalReviewPolicy
{
    public const string OperationKind = "msix-current-user-uninstall";
    public const string DeploymentApi =
        "Windows.Management.Deployment.PackageManager.RemovePackageAsync";
    public const string RemovalOptions = "None";
    public const string Scope = "Current Windows user";
    public static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
    {
        "Arm",
        "Arm64",
        "Neutral",
        "X64",
        "X86",
        "X86OnArm64",
        "Unknown",
    };

    public static PackageRemovalReview Create(
        string packageName,
        string packageFullName,
        string publisher,
        string publisherId,
        string version,
        string architecture,
        DateTimeOffset reviewedUtc)
    {
        var expiresUtc = reviewedUtc + ReviewLifetime;
        var review = new PackageRemovalReview(
            packageName,
            packageFullName,
            publisher,
            publisherId,
            version,
            architecture,
            reviewedUtc,
            expiresUtc,
            string.Empty);
        return review with { ReviewSha256 = ComputeDigest(review) };
    }

    public static void Require(PackageRemovalReview? review)
    {
        if (review is null ||
            !IsBoundedText(review.PackageName, 128) ||
            !IsBoundedText(review.PackageFullName, 512) ||
            !IsBoundedText(review.Publisher, 512) ||
            !IsBoundedText(review.PublisherId, 128) ||
            !IsFourPartVersion(review.Version) ||
            !Architectures.Contains(review.Architecture) ||
            review.ReviewedUtc.Offset != TimeSpan.Zero ||
            review.ExpiresUtc.Offset != TimeSpan.Zero ||
            review.ExpiresUtc - review.ReviewedUtc != ReviewLifetime ||
            !IsLowerSha256(review.ReviewSha256) ||
            !string.Equals(
                review.ReviewSha256,
                ComputeDigest(review with { ReviewSha256 = string.Empty }),
                StringComparison.Ordinal))
        {
            throw new PackageRemovalWorkflowException("package_removal_review_invalid");
        }
    }

    public static void RequireCurrent(
        PackageRemovalReview? review,
        string expectedReviewSha256,
        DateTimeOffset now)
    {
        Require(review);
        if (!IsLowerSha256(expectedReviewSha256) ||
            !string.Equals(review!.ReviewSha256, expectedReviewSha256, StringComparison.Ordinal))
        {
            throw new PackageRemovalWorkflowException("package_removal_review_changed");
        }

        if (now.Offset != TimeSpan.Zero || now < review.ReviewedUtc || now > review.ExpiresUtc)
        {
            throw new PackageRemovalWorkflowException("package_removal_review_expired");
        }
    }

    private static string ComputeDigest(PackageRemovalReview review)
    {
        var canonical = string.Join(
            '\n',
            "dcx-package-removal-review-v1",
            OperationKind,
            DeploymentApi,
            RemovalOptions,
            Scope,
            review.PackageName,
            review.PackageFullName,
            review.Publisher,
            review.PublisherId,
            review.Version,
            review.Architecture,
            review.ReviewedUtc.ToString("O", CultureInfo.InvariantCulture),
            review.ExpiresUtc.ToString("O", CultureInfo.InvariantCulture),
            "application-termination-expected=true",
            "cancellation-after-entry=false",
            "application-data-preserved=false",
            "unused-dependencies-may-be-removed=true",
            "requests-elevation=false");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool IsBoundedText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) && value.All(character =>
            !char.IsControl(character) && character is not ('\u202A' or '\u202B' or '\u202C' or
                '\u202D' or '\u202E' or '\u2066' or '\u2067' or '\u2068' or '\u2069'));

    private static bool IsFourPartVersion(string? value)
    {
        var parts = value?.Split('.', StringSplitOptions.None);
        return parts is { Length: 4 } && parts.All(part =>
            ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            (part == "0" || !part.StartsWith('0')));
    }

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public enum PackageRemovalExecutionStatus
{
    ValidationNoOp,
    Removed,
}

public sealed record PackageRemovalWorkflowResult(
    PackageRemovalExecutionStatus Status,
    string ReviewSha256,
    bool ExecutionPermitted,
    int PackageManagerInvocations,
    bool RemovalCompleted,
    bool IsRegistered,
    bool IsCurrentUserOnly,
    bool TargetsCallingPackage,
    bool ApplicationTerminationExpected,
    bool CancellationSupportedAfterEntry,
    bool RequestsElevation,
    int InstallerProcessesStarted,
    int ProductionActions);

public static class PackageRemovalWorkflowResultPolicy
{
    public static void Require(
        PackageRemovalReview? review,
        PackageRemovalWorkflowResult? result)
    {
        PackageRemovalReviewPolicy.Require(review);
        if (result is null ||
            !string.Equals(review!.ReviewSha256, result.ReviewSha256, StringComparison.Ordinal) ||
            result.RequestsElevation || result.InstallerProcessesStarted != 0 ||
            result.ProductionActions is < 0 or > 1)
        {
            throw new PackageRemovalWorkflowException("package_removal_result_invalid");
        }

        var valid = result.Status switch
        {
            PackageRemovalExecutionStatus.ValidationNoOp =>
                !result.ExecutionPermitted && result.PackageManagerInvocations == 0 &&
                !result.RemovalCompleted && result.IsRegistered &&
                result.IsCurrentUserOnly && result.TargetsCallingPackage &&
                !result.ApplicationTerminationExpected &&
                !result.CancellationSupportedAfterEntry && result.ProductionActions == 0,
            PackageRemovalExecutionStatus.Removed =>
                result.ExecutionPermitted && result.PackageManagerInvocations == 1 &&
                result.RemovalCompleted && !result.IsRegistered &&
                result.IsCurrentUserOnly && result.TargetsCallingPackage &&
                result.ApplicationTerminationExpected &&
                !result.CancellationSupportedAfterEntry && result.ProductionActions == 1,
            _ => false,
        };
        if (!valid)
        {
            throw new PackageRemovalWorkflowException("package_removal_result_invalid");
        }
    }
}

public interface IPackageRemovalWorkflow
{
    PackageRemovalSourceKind SourceKind { get; }
    PackageRemovalAvailability Availability { get; }
    PackageRemovalReview? Review { get; }

    Task<PackageRemovalWorkflowResult> ExecuteAsync(
        string expectedReviewSha256,
        DateTimeOffset now,
        bool consequencesAcknowledged,
        bool backupChoiceAcknowledged,
        CancellationToken cancellationToken);
}

public sealed class UnavailablePackageRemovalWorkflow(
    PackageRemovalAvailability availability,
    PackageRemovalReview? review = null)
    : IPackageRemovalWorkflow
{
    public PackageRemovalSourceKind SourceKind => PackageRemovalSourceKind.Unavailable;
    public PackageRemovalAvailability Availability { get; } =
        availability is PackageRemovalAvailability.NotPackaged or
            PackageRemovalAvailability.ProductionIdentityNotConfigured
            ? availability
            : throw new ArgumentOutOfRangeException(nameof(availability));
    public PackageRemovalReview? Review { get; } = ValidateOptionalReview(review);

    public Task<PackageRemovalWorkflowResult> ExecuteAsync(
        string expectedReviewSha256,
        DateTimeOffset now,
        bool consequencesAcknowledged,
        bool backupChoiceAcknowledged,
        CancellationToken cancellationToken) =>
        Task.FromException<PackageRemovalWorkflowResult>(
            new PackageRemovalWorkflowException("package_removal_unavailable"));

    private static PackageRemovalReview? ValidateOptionalReview(PackageRemovalReview? review)
    {
        if (review is not null)
        {
            PackageRemovalReviewPolicy.Require(review);
        }

        return review;
    }
}

public sealed class ValidationPackageRemovalWorkflow(PackageRemovalReview review)
    : PackageRemovalWorkflowBase(review)
{
    public override PackageRemovalSourceKind SourceKind => PackageRemovalSourceKind.GeneratedFixture;
    public override PackageRemovalAvailability Availability => PackageRemovalAvailability.Available;

    protected override Task<PackageRemovalWorkflowResult> ExecuteCoreAsync(
        CancellationToken cancellationToken) => Task.FromResult(new PackageRemovalWorkflowResult(
            PackageRemovalExecutionStatus.ValidationNoOp,
            Review.ReviewSha256,
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
            ProductionActions: 0));
}

public sealed class ApprovedCurrentPackageRemovalWorkflow(
    PackageRemovalReview review,
    ICurrentUserPackageRemovalExecutor executor)
    : PackageRemovalWorkflowBase(review)
{
    private readonly ICurrentUserPackageRemovalExecutor executor =
        executor ?? throw new ArgumentNullException(nameof(executor));

    public override PackageRemovalSourceKind SourceKind =>
        PackageRemovalSourceKind.ApprovedCurrentPackage;
    public override PackageRemovalAvailability Availability => PackageRemovalAvailability.Available;

    protected override async Task<PackageRemovalWorkflowResult> ExecuteCoreAsync(
        CancellationToken cancellationToken)
    {
        CurrentUserPackageRemovalExecutionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(
                    Review.PackageFullName,
                    cancellationToken)
                .ConfigureAwait(false);
            CurrentUserPackageRemovalExecutionPolicy.Require(execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new PackageRemovalWorkflowException("package_removal_execution_failed");
        }

        var result = new PackageRemovalWorkflowResult(
            PackageRemovalExecutionStatus.Removed,
            Review.ReviewSha256,
            ExecutionPermitted: true,
            execution.PackageManagerInvocations,
            RemovalCompleted: true,
            execution.IsRegistered,
            execution.IsCurrentUserOnly,
            execution.TargetsCallingPackage,
            execution.ApplicationTerminationExpected,
            execution.CancellationSupportedAfterEntry,
            execution.RequestsElevation,
            execution.InstallerProcessesStarted,
            ProductionActions: 1);
        PackageRemovalWorkflowResultPolicy.Require(Review, result);
        return result;
    }
}

public abstract class PackageRemovalWorkflowBase : IPackageRemovalWorkflow
{
    private int consumed;

    protected PackageRemovalWorkflowBase(PackageRemovalReview review)
    {
        PackageRemovalReviewPolicy.Require(review);
        Review = review;
    }

    public abstract PackageRemovalSourceKind SourceKind { get; }
    public abstract PackageRemovalAvailability Availability { get; }
    public PackageRemovalReview Review { get; }

    PackageRemovalReview? IPackageRemovalWorkflow.Review => Review;

    public async Task<PackageRemovalWorkflowResult> ExecuteAsync(
        string expectedReviewSha256,
        DateTimeOffset now,
        bool consequencesAcknowledged,
        bool backupChoiceAcknowledged,
        CancellationToken cancellationToken)
    {
        PackageRemovalReviewPolicy.RequireCurrent(Review, expectedReviewSha256, now);
        if (!consequencesAcknowledged)
        {
            throw new PackageRemovalWorkflowException(
                "package_removal_consequences_acknowledgment_required");
        }

        if (!backupChoiceAcknowledged)
        {
            throw new PackageRemovalWorkflowException(
                "package_removal_backup_acknowledgment_required");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
        {
            throw new PackageRemovalWorkflowException("package_removal_request_consumed");
        }

        var result = await ExecuteCoreAsync(cancellationToken).ConfigureAwait(false);
        PackageRemovalWorkflowResultPolicy.Require(Review, result);
        return result;
    }

    protected abstract Task<PackageRemovalWorkflowResult> ExecuteCoreAsync(
        CancellationToken cancellationToken);
}

public sealed class PackageRemovalWorkflowException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
