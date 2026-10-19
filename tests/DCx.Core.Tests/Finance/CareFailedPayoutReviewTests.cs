using System.Text.Json;
using DCx.Core.Finance;

namespace DCx.Core.Tests.Finance;

public sealed class CareFailedPayoutReviewTests
{
    private static readonly DateTimeOffset CapturedAt =
        DateTimeOffset.Parse("2026-10-18T03:00:00Z");

    [Fact]
    public void Policy_AcceptsPrivacyMinimizedReviewAndZeroAuthorityDisposition()
    {
        var review = Review();
        CareFailedPayoutReviewPolicy.RequireList([review]);
        var request = new CareFailedPayoutDispositionRequest(
            review.ReviewId,
            review.ReviewSha256,
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            ExplicitlyApproved: true);
        CareFailedPayoutReviewPolicy.RequireRequest(request);
        CareFailedPayoutReviewPolicy.RequireResult(new CareFailedPayoutDispositionResult(
            review.ReviewId,
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
            CapturedAt.AddMinutes(1),
            ProviderCalls: 0,
            FinancialMutations: 0,
            PayoutAttempts: 0,
            NotificationTransports: 0), request);

        var json = JsonSerializer.Serialize(review);
        Assert.DoesNotContain("@", json, StringComparison.Ordinal);
        Assert.DoesNotContain("destination", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pseudonym", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("batch", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Policy_RejectsMalformedIdentityAmountStateAndSensitiveShapedCodes()
    {
        var review = Review();

        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with { ReviewId = "ABC" }]));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with { AmountCents = 0 }]));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with { FailureCode = "person@example.test" }]));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with { HoldCode = "released" }]));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with { FailedAtUtc = default }]));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(
                [review with
                {
                    DispositionCode = CareFailedPayoutReviewPolicy.RetainHold,
                    NoticeCode = CareFailedPayoutReviewPolicy.NoticeNotReady,
                }]));
    }

    [Fact]
    public void Policy_RejectsDuplicateOrExcessReviewsAndUnapprovedDisposition()
    {
        var review = Review();
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList([review, review]));
        var excess = Enumerable.Range(0, CareFailedPayoutReviewPolicy.MaximumReviews + 1)
            .Select(index => review with { ReviewId = index.ToString("x32") })
            .ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireList(excess));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireRequest(new CareFailedPayoutDispositionRequest(
                review.ReviewId,
                review.ReviewSha256,
                CareFailedPayoutReviewPolicy.RetainHold,
                ExplicitlyApproved: false)));
    }

    [Fact]
    public void Policy_RejectsAnyDispositionAuthorityCounter()
    {
        var result = new CareFailedPayoutDispositionResult(
            Review().ReviewId,
            CareFailedPayoutReviewPolicy.RetainHold,
            CareFailedPayoutReviewPolicy.LocalDashboardNoticeReady,
            CapturedAt,
            ProviderCalls: 0,
            FinancialMutations: 0,
            PayoutAttempts: 0,
            NotificationTransports: 0);
        CareFailedPayoutReviewPolicy.RequireResult(result);

        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result with { ProviderCalls = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result with { FinancialMutations = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result with { PayoutAttempts = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result with { NotificationTransports = 1 }));
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result with { RecordedUtc = default }));
        var request = new CareFailedPayoutDispositionRequest(
            result.ReviewId,
            Review().ReviewSha256,
            CareFailedPayoutReviewPolicy.RequestRecipientUpdate,
            ExplicitlyApproved: true);
        Assert.Throws<InvalidOperationException>(() =>
            CareFailedPayoutReviewPolicy.RequireResult(result, request));
    }

    [Fact]
    public async Task UnavailableSource_DoesNotInventReviewsOrRecordDisposition()
    {
        var source = UnavailableCareFailedPayoutReviewSource.Instance;
        Assert.Empty(await source.ListFailedPayoutReviewsAsync());
        var review = Review();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.RecordFailedPayoutDispositionAsync(
                new CareFailedPayoutDispositionRequest(
                    review.ReviewId,
                    review.ReviewSha256,
                    CareFailedPayoutReviewPolicy.RetainHold,
                    ExplicitlyApproved: true)));
    }

    private static CareFailedPayoutReview Review() => new(
        "fedcba9876543210fedcba9876543210",
        "care:2026-05",
        700,
        CareFailedPayoutReviewPolicy.RecipientUnverified,
        CareFailedPayoutReviewPolicy.FailedItemHold,
        CareFailedPayoutReviewPolicy.NoticeNotReady,
        CapturedAt,
        "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef",
        CareFailedPayoutReviewPolicy.Unreviewed);
}
