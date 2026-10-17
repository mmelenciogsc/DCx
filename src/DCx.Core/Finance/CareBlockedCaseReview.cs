namespace DCx.Core.Finance;

public sealed record CareBlockedCaseReview(
    string CaseId,
    string EventKind,
    string ReasonCode,
    int AttemptCount,
    DateTimeOffset FirstReceivedUtc,
    DateTimeOffset? LastAttemptUtc,
    string ReviewSha256,
    string DispositionCode);

public sealed record CareBlockedCaseDispositionRequest(
    string CaseId,
    string ReviewedSha256,
    string DispositionCode,
    bool ExplicitlyApproved);

public sealed record CareBlockedCaseDispositionResult(
    string CaseId,
    string DispositionCode,
    DateTimeOffset RecordedUtc,
    int ProviderCalls,
    int FinancialMutations);

public interface ICareBlockedCaseReviewSource
{
    Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default);

    Task<CareBlockedCaseDispositionResult> RecordDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        CancellationToken cancellationToken = default);
}

public static class CareBlockedCaseReviewPolicy
{
    public const int MaximumCases = 100;
    public const string Unreviewed = "unreviewed";
    public const string InvestigationRequested = "investigation_requested";
    public const string RetainedBlocked = "retained_blocked";

    public static void RequireReview(CareBlockedCaseReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!IsCaseId(review.CaseId) ||
            review.EventKind is not ("sale" or "refund" or "reversal") ||
            review.ReasonCode is not ("identity_conflict" or "policy_rejected") ||
            review.AttemptCount is < 1 or > 1_000_000 ||
            review.FirstReceivedUtc.Offset != TimeSpan.Zero ||
            review.LastAttemptUtc is not { Offset: var lastOffset } ||
            lastOffset != TimeSpan.Zero ||
            review.LastAttemptUtc < review.FirstReceivedUtc ||
            !IsSha256(review.ReviewSha256) ||
            !IsDisposition(review.DispositionCode, permitUnreviewed: true))
        {
            throw new InvalidOperationException("The CARE blocked-case review contract is invalid.");
        }
    }

    public static void RequireRequest(CareBlockedCaseDispositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsCaseId(request.CaseId) ||
            !IsSha256(request.ReviewedSha256) ||
            !IsDisposition(request.DispositionCode, permitUnreviewed: false))
        {
            throw new InvalidOperationException("The CARE blocked-case disposition request is invalid.");
        }
    }

    public static void RequireResult(CareBlockedCaseDispositionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!IsCaseId(result.CaseId) ||
            !IsDisposition(result.DispositionCode, permitUnreviewed: false) ||
            result.RecordedUtc.Offset != TimeSpan.Zero ||
            result.ProviderCalls != 0 ||
            result.FinancialMutations != 0)
        {
            throw new InvalidOperationException("The CARE blocked-case disposition result is invalid.");
        }
    }

    public static void RequireList(IReadOnlyList<CareBlockedCaseReview> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count > MaximumCases ||
            cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            throw new InvalidOperationException("The CARE blocked-case review list is invalid.");
        }

        foreach (var item in cases) RequireReview(item);
    }

    private static bool IsCaseId(string? value) =>
        value is { Length: 32 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsDisposition(string? value, bool permitUnreviewed) =>
        value == InvestigationRequested || value == RetainedBlocked ||
        permitUnreviewed && value == Unreviewed;
}

public sealed class UnavailableCareBlockedCaseReviewSource : ICareBlockedCaseReviewSource
{
    public static UnavailableCareBlockedCaseReviewSource Instance { get; } = new();

    private UnavailableCareBlockedCaseReviewSource()
    {
    }

    public Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("CARE blocked-case review is not configured.");
    }

    public Task<CareBlockedCaseDispositionResult> RecordDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("CARE blocked-case review is not configured.");
    }
}
