using DCx.Core.Finance;

namespace DCx.Desktop;

internal sealed class CareOperationsFixtureSource :
    ICareOperationsSource,
    ICareBlockedCaseReviewSource,
    ICareFailedPayoutReviewSource
{
    internal const string EnvironmentVariable = "DCX_CARE_OPERATIONS_FIXTURE";
    private static readonly DateTimeOffset FixtureTime =
        DateTimeOffset.Parse("2026-10-18T01:00:00Z");
    private int processed;
    private readonly object dispositionLock = new();
    private string disposition = CareBlockedCaseReviewPolicy.Unreviewed;
    private readonly object failedPayoutLock = new();
    private string failedPayoutDisposition = CareFailedPayoutReviewPolicy.Unreviewed;
    private string failedPayoutNotice = CareFailedPayoutReviewPolicy.NoticeNotReady;
    private static readonly CareBlockedCaseReview BlockedFixture = new(
        "0123456789abcdef0123456789abcdef",
        "refund",
        "policy_rejected",
        3,
        FixtureTime.AddMinutes(-10),
        FixtureTime.AddMinutes(-1),
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
        CareBlockedCaseReviewPolicy.Unreviewed);
    private static readonly CareFailedPayoutReview FailedPayoutFixture = new(
        "fedcba9876543210fedcba9876543210",
        "care:2026-05",
        700,
        CareFailedPayoutReviewPolicy.RecipientUnverified,
        CareFailedPayoutReviewPolicy.FailedItemHold,
        CareFailedPayoutReviewPolicy.NoticeNotReady,
        FixtureTime.AddMinutes(-20),
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
        CareFailedPayoutReviewPolicy.Unreviewed);

    public Task<CareOperationsHealth> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(CurrentHealth());
    }

    public Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        CareOperationsPolicy.RequireHealth(reviewedHealth);
        cancellationToken.ThrowIfCancellationRequested();
        if (!explicitlyApproved)
        {
            throw new InvalidOperationException(
                "Explicit CARE operations approval is required.");
        }

        if (reviewedHealth != CurrentHealth() ||
            Interlocked.CompareExchange(ref processed, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "The reviewed CARE operations state changed before processing.");
        }

        return Task.FromResult(new CareOperationsProcessResult(
            2,
            2,
            0,
            0,
            0,
            CurrentHealth()));
    }

    public Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (dispositionLock)
        {
            IReadOnlyList<CareBlockedCaseReview> result =
                [BlockedFixture with { DispositionCode = disposition }];
            CareBlockedCaseReviewPolicy.RequireList(result);
            return Task.FromResult(result);
        }
    }

    public Task<CareBlockedCaseDispositionResult> RecordDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CareBlockedCaseReviewPolicy.RequireRequest(request);
        if (!request.ExplicitlyApproved)
        {
            throw new InvalidOperationException(
                "Explicit CARE blocked-case approval is required.");
        }

        if (!string.Equals(request.CaseId, BlockedFixture.CaseId, StringComparison.Ordinal) ||
            !string.Equals(
                request.ReviewedSha256, BlockedFixture.ReviewSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reviewed CARE blocked case changed before disposition.");
        }

        lock (dispositionLock)
        {
            disposition = request.DispositionCode;
        }

        var result = new CareBlockedCaseDispositionResult(
            request.CaseId,
            request.DispositionCode,
            FixtureTime.AddMinutes(1),
            ProviderCalls: 0,
            FinancialMutations: 0);
        CareBlockedCaseReviewPolicy.RequireResult(result);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (failedPayoutLock)
        {
            IReadOnlyList<CareFailedPayoutReview> result =
                [FailedPayoutFixture with
                {
                    DispositionCode = failedPayoutDisposition,
                    NoticeCode = failedPayoutNotice,
                }];
            CareFailedPayoutReviewPolicy.RequireList(result);
            return Task.FromResult(result);
        }
    }

    public Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CareFailedPayoutReviewPolicy.RequireRequest(request);
        if (!string.Equals(
                request.ReviewId,
                FailedPayoutFixture.ReviewId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.ReviewedSha256,
                FailedPayoutFixture.ReviewSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The reviewed failed payout changed before disposition.");
        }

        lock (failedPayoutLock)
        {
            if (!string.Equals(
                    failedPayoutDisposition,
                    CareFailedPayoutReviewPolicy.Unreviewed,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The failed-payout decision was already recorded.");
            }

            failedPayoutDisposition = request.DispositionCode;
            failedPayoutNotice = CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady;
        }

        var result = new CareFailedPayoutDispositionResult(
            request.ReviewId,
            request.DispositionCode,
            CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
            FixtureTime.AddMinutes(2),
            ProviderCalls: 0,
            FinancialMutations: 0,
            PayoutAttempts: 0,
            NotificationTransports: 0);
        CareFailedPayoutReviewPolicy.RequireResult(result, request);
        return Task.FromResult(result);
    }

    private CareOperationsHealth CurrentHealth() => Volatile.Read(ref processed) == 0
        ? new CareOperationsHealth(
            "operator_action_required", 2, 2, 1, 0, 3, FixtureTime, FixtureTime, true)
        : new CareOperationsHealth(
            "operator_action_required", 0, 0, 1, 2, 3, null, null, false);
}

internal static class CareOperationsSourceFactory
{
    public static (ICareOperationsSource Source, bool IsFixture) Create()
    {
        var fixture = string.Equals(
            Environment.GetEnvironmentVariable(CareOperationsFixtureSource.EnvironmentVariable),
            "1",
            StringComparison.Ordinal);
        return fixture
            ? (new CareOperationsFixtureSource(), true)
            : (UnavailableCareOperationsSource.Instance, false);
    }
}
