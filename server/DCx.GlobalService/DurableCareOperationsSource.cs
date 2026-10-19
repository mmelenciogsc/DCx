using DCx.Core.Finance;

namespace DCx.GlobalService;

/// <summary>
/// Exposes aggregate verified-event quarantine health and an explicitly gated,
/// local-only replay operation. It never contacts a payment provider.
/// </summary>
public sealed class DurableCareOperationsSource :
    ICareOperationsSource,
    ICareBlockedCaseReviewSource,
    ICareFailedPayoutReviewSource
{
    private readonly DurableCareRepository repository;
    private readonly bool processingCapabilityEnabled;
    private readonly TimeProvider timeProvider;
    private readonly int processLimit;
    private readonly string? operatorPseudonymSha256;

    public DurableCareOperationsSource(
        DurableCareRepository repository,
        bool processingCapabilityEnabled = false,
        TimeProvider? timeProvider = null,
        int processLimit = CareOperationsPolicy.MaximumProcessBatch,
        string? operatorPseudonymSha256 = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        if (processLimit is < 1 or > CareOperationsPolicy.MaximumProcessBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(processLimit));
        }

        this.processingCapabilityEnabled = processingCapabilityEnabled;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.processLimit = processLimit;
        this.operatorPseudonymSha256 = operatorPseudonymSha256 is null
            ? null
            : CareOperatorActionAuditIntegrity.NormalizeOperatorPseudonymSha256(
                operatorPseudonymSha256);
    }

    public async Task<CareOperationsHealth> RefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var health = ToCareOperationsHealth(
            await repository.GetOfficialWebhookQuarantineHealthAsync(
                    timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false),
            processingCapabilityEnabled);
        CareOperationsPolicy.RequireHealth(health);
        return health;
    }

    public Task<CareOperationsProcessResult> ProcessDueAsync(
        CareOperationsHealth reviewedHealth,
        bool explicitlyApproved,
        CancellationToken cancellationToken = default) =>
        repository.ProcessReviewedOfficialWebhookQuarantineAsync(
            reviewedHealth,
            explicitlyApproved,
            processingCapabilityEnabled,
            timeProvider.GetUtcNow(),
            processLimit,
            operatorPseudonymSha256,
            cancellationToken);

    public Task<IReadOnlyList<CareBlockedCaseReview>> ListBlockedCasesAsync(
        CancellationToken cancellationToken = default) =>
        repository.ListBlockedCasesAsync(cancellationToken);

    public Task<CareBlockedCaseDispositionResult> RecordDispositionAsync(
        CareBlockedCaseDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (operatorPseudonymSha256 is null)
        {
            throw new InvalidOperationException(
                "CARE blocked-case disposition identity is not configured.");
        }

        return repository.RecordBlockedCaseDispositionAsync(
            request,
            operatorPseudonymSha256,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public Task<IReadOnlyList<CareFailedPayoutReview>> ListFailedPayoutReviewsAsync(
        CancellationToken cancellationToken = default) =>
        repository.ListFailedPayoutReviewsAsync(cancellationToken);

    public Task<CareFailedPayoutDispositionResult> RecordFailedPayoutDispositionAsync(
        CareFailedPayoutDispositionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (operatorPseudonymSha256 is null)
        {
            throw new InvalidOperationException(
                "CARE failed-payout disposition identity is not configured.");
        }

        return repository.RecordFailedPayoutDispositionAsync(
            request,
            operatorPseudonymSha256,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    internal static CareOperationsHealth ToCareOperationsHealth(
        WebhookQuarantineHealthSnapshot snapshot,
        bool processingCapabilityEnabled)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var health = new CareOperationsHealth(
            snapshot.StatusCode,
            snapshot.Pending,
            snapshot.Due,
            snapshot.Blocked,
            snapshot.Completed,
            snapshot.MaximumAttemptCount,
            snapshot.OldestPendingUtc,
            snapshot.NextAttemptUtc,
            processingCapabilityEnabled && snapshot.Due > 0);
        CareOperationsPolicy.RequireHealth(health);
        return health;
    }
}
