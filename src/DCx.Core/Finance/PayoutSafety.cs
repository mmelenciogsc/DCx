namespace DCx.Core.Finance;

public enum PayoutExecutionMode
{
    Sandbox = 0,
    Live = 1,
}

public sealed record PayoutItem(string ContributorPseudonym, long AmountCents);

public sealed record PayoutBatchPreview(
    string BatchId,
    PayoutExecutionMode Mode,
    DateOnly EligibilityDate,
    IReadOnlyList<PayoutItem> Items)
{
    public long TotalCents => Items.Sum(item => item.AmountCents);
}

public sealed record PayoutBatchResult(string BatchId, bool Succeeded, string ProviderStatus);

public interface IPayoutSandboxProvider
{
    Task<PayoutBatchResult> SendSandboxAsync(PayoutBatchPreview preview, CancellationToken cancellationToken);
}

public sealed class PayoutSafetyCoordinator(IPayoutSandboxProvider sandboxProvider)
{
    private readonly HashSet<string> executedBatches = new(StringComparer.Ordinal);

    public async Task<PayoutBatchResult> ExecuteAsync(
        PayoutBatchPreview preview,
        bool accountEligibilityVerified,
        bool ownerApproved,
        DateOnly today,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Mode != PayoutExecutionMode.Sandbox)
        {
            throw new InvalidOperationException("Live payouts are disabled in this build.");
        }

        if (!accountEligibilityVerified || !ownerApproved || today < preview.EligibilityDate ||
            preview.Items.Count == 0 || preview.Items.Any(item => item.AmountCents <= 0) ||
            preview.Items.Select(item => item.ContributorPseudonym).Distinct(StringComparer.Ordinal).Count() != preview.Items.Count)
        {
            throw new InvalidOperationException("The sandbox payout eligibility or owner-approval gate is not satisfied.");
        }

        if (!executedBatches.Add(preview.BatchId))
        {
            throw new InvalidOperationException("The payout batch has already been executed.");
        }

        var result = await sandboxProvider.SendSandboxAsync(preview, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.BatchId, preview.BatchId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The sandbox provider returned a conflicting batch identifier.");
        }

        return result;
    }
}
