namespace DCx.Core.Finance;

public sealed record CareFailedPayoutReview(
    string ReviewId,
    string CarePeriodId,
    long AmountCents,
    string FailureCode,
    string HoldCode,
    string NoticeCode,
    DateTimeOffset FailedAtUtc,
    string ReviewSha256,
    string DispositionCode);

public sealed record CareFailedPayoutDispositionRequest(
    string ReviewId,
    string ReviewedSha256,
    string DispositionCode,
    bool ExplicitlyApproved);

public sealed record CareFailedPayoutDispositionResult(
    string ReviewId,
    string DispositionCode,
    string NoticeCode,
    DateTimeOffset RecordedUtc,
    int ProviderCalls,
    int FinancialMutations,
    int PayoutAttempts,
    int NotificationTransports);

public interface ICareFailedPayoutReviewSource
{
    Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default);

    Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default);
}

public static class CareFailedPayoutReviewPolicy
{
    public const int MaximumReviews = 100;
    public const long MaximumAmountCents = 100_000_000;
    public const string RecipientUnverified = "recipient_unverified";
    public const string ProviderRejected = "provider_rejected";
    public const string Returned = "returned";
    public const string FailedItemHold = "failed_item_hold";
    public const string NoticeNotReady = "not_ready";
    public const string LocalDashboardNoticeReady = "local_dashboard_notice_ready";
    public const string Unreviewed = "unreviewed";
    public const string RetainHold = "retain_hold";
    public const string RequestRecipientUpdate = "request_recipient_update";
    public const string CancelRetryIntent = "cancel_retry_intent";

    public static void RequireList(IReadOnlyList<CareFailedPayoutReview> reviews)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        if (reviews.Count > MaximumReviews)
        {
            throw new InvalidOperationException(
                "The failed-payout review list exceeds its bound.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var review in reviews)
        {
            RequireReview(review);
            if (!ids.Add(review.ReviewId))
            {
                throw new InvalidOperationException(
                    "Failed-payout review references must be unique.");
            }
        }
    }

    public static void RequireRequest(CareFailedPayoutDispositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReviewId(request.ReviewId);
        RequireDigest(request.ReviewedSha256);
        if (!request.ExplicitlyApproved ||
            request.DispositionCode is not (
                RetainHold or RequestRecipientUpdate or CancelRetryIntent))
        {
            throw new InvalidOperationException(
                "A failed-payout disposition requires exact review and explicit approval.");
        }
    }

    public static void RequireResult(CareFailedPayoutDispositionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireReviewId(result.ReviewId);
        if (result.DispositionCode is not (
                RetainHold or RequestRecipientUpdate or CancelRetryIntent) ||
            !string.Equals(
                result.NoticeCode,
                LocalDashboardNoticeReady,
                StringComparison.Ordinal) ||
            result.RecordedUtc == default ||
            result.RecordedUtc.Offset != TimeSpan.Zero ||
            result.ProviderCalls != 0 ||
            result.FinancialMutations != 0 ||
            result.PayoutAttempts != 0 ||
            result.NotificationTransports != 0)
        {
            throw new InvalidOperationException(
                "The failed-payout disposition result exceeds its zero-authority contract.");
        }
    }

    public static void RequireResult(
        CareFailedPayoutDispositionResult result,
        CareFailedPayoutDispositionRequest request)
    {
        RequireResult(result);
        RequireRequest(request);
        if (!string.Equals(result.ReviewId, request.ReviewId, StringComparison.Ordinal) ||
            !string.Equals(
                result.DispositionCode,
                request.DispositionCode,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The failed-payout disposition result does not match the exact request.");
        }
    }

    private static void RequireReview(CareFailedPayoutReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        RequireReviewId(review.ReviewId);
        PayoutContractValidation.RequireCarePeriodId(review.CarePeriodId);
        RequireDigest(review.ReviewSha256);
        if (review.AmountCents is <= 0 or > MaximumAmountCents ||
            review.FailureCode is not (
                RecipientUnverified or ProviderRejected or Returned) ||
            !string.Equals(review.HoldCode, FailedItemHold, StringComparison.Ordinal) ||
            review.NoticeCode is not (NoticeNotReady or LocalDashboardNoticeReady) ||
            review.FailedAtUtc == default ||
            review.FailedAtUtc.Offset != TimeSpan.Zero ||
            review.DispositionCode is not (
                Unreviewed or RetainHold or RequestRecipientUpdate or CancelRetryIntent) ||
            (review.DispositionCode == Unreviewed &&
                !string.Equals(review.NoticeCode, NoticeNotReady, StringComparison.Ordinal)) ||
            (review.DispositionCode != Unreviewed &&
                !string.Equals(
                    review.NoticeCode,
                    LocalDashboardNoticeReady,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "The failed-payout review contract is invalid.");
        }
    }

    private static void RequireReviewId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 32 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A failed-payout review requires a lowercase opaque reference.");
        }
    }

    private static void RequireDigest(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A failed-payout review requires a lowercase SHA-256 digest.");
        }
    }
}

public sealed class UnavailableCareFailedPayoutReviewSource : ICareFailedPayoutReviewSource
{
    public static UnavailableCareFailedPayoutReviewSource Instance { get; } = new();

    private UnavailableCareFailedPayoutReviewSource()
    {
    }

    public Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<CareFailedPayoutReview>>([]);
    }

    public Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Failed-payout review is not configured.");
    }
}
