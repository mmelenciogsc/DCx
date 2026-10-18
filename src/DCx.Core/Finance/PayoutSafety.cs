using System.Security.Cryptography;
using System.Text;

namespace DCx.Core.Finance;

public enum PayoutExecutionMode
{
    Sandbox = 0,
    Live = 1,
}

public sealed record PayoutRecipientApproval(string SnapshotId, string DestinationSha256)
{
    public static PayoutRecipientApproval Create(string snapshotId, string exactDestination)
    {
        PayoutContractValidation.RequireSnapshotId(snapshotId);
        if (string.IsNullOrWhiteSpace(exactDestination) || exactDestination.Length > 512 ||
            exactDestination.Any(character => character is < ' ' or > '~'))
        {
            throw new ArgumentException("A bounded printable payout destination is required.", nameof(exactDestination));
        }

        var bytes = Encoding.UTF8.GetBytes(exactDestination);
        try
        {
            return new PayoutRecipientApproval(
                snapshotId,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

public sealed record PayoutItem(
    string ContributorPseudonym,
    long AmountCents,
    PayoutRecipientApproval RecipientApproval);

public sealed record PayoutBatchPreview(
    string BatchId,
    PayoutExecutionMode Mode,
    DateOnly EligibilityDate,
    IReadOnlyList<PayoutItem> Items,
    string? CarePeriodId = null)
{
    public long TotalCents => Items.Sum(item => item.AmountCents);
}

public enum PayoutBatchOutcome
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    PartiallySucceeded = 3,
}

public sealed record PayoutItemResult(
    string ContributorPseudonym,
    long AmountCents,
    bool Succeeded,
    string ProviderStatus);

public sealed record PayoutBatchResult(
    string BatchId,
    PayoutBatchOutcome Outcome,
    string ProviderStatus,
    IReadOnlyList<PayoutItemResult>? ItemResults = null)
{
    public bool Succeeded => Outcome == PayoutBatchOutcome.Succeeded;

    public bool IsTerminal => Outcome != PayoutBatchOutcome.Pending;
}

public interface IPayoutSandboxProvider
{
    Task<PayoutBatchResult> SendSandboxAsync(PayoutBatchPreview preview, CancellationToken cancellationToken);
}

public enum SandboxPayoutLookupState
{
    NotFound,
    Pending,
    Succeeded,
    Failed,
    PartiallySucceeded,
}

public sealed record SandboxPayoutLookupResult(
    string BatchId,
    SandboxPayoutLookupState State,
    string? ProviderStatus,
    IReadOnlyList<PayoutItemResult>? ItemResults = null);

public interface IReconcilablePayoutSandboxProvider : IPayoutSandboxProvider
{
    Task<SandboxPayoutLookupResult> LookupSandboxAsync(
        PayoutBatchPreview preview,
        CancellationToken cancellationToken);
}

public static class PayoutContractValidation
{
    public static void RequirePreview(PayoutBatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Mode != PayoutExecutionMode.Sandbox ||
            string.IsNullOrWhiteSpace(preview.BatchId) || preview.BatchId.Length > 128 ||
            !IsIdentifier(preview.BatchId) || preview.Items is null ||
            preview.Items.Count == 0 || preview.Items.Count > 100 ||
            preview.Items.Any(item => item is null || item.AmountCents is <= 0 or > 1_000_000 ||
                string.IsNullOrWhiteSpace(item.ContributorPseudonym) ||
                item.ContributorPseudonym.Length > 128 ||
                !IsIdentifier(item.ContributorPseudonym) || item.RecipientApproval is null) ||
            preview.Items.Select(item => item.ContributorPseudonym)
                .Distinct(StringComparer.Ordinal).Count() != preview.Items.Count ||
            preview.Items.Select(item => item.RecipientApproval.SnapshotId)
                .Distinct(StringComparer.Ordinal).Count() != preview.Items.Count)
        {
            throw new InvalidOperationException("The sandbox payout preview is invalid.");
        }

        long total = 0;
        foreach (var item in preview.Items)
        {
            RequireSnapshotId(item.RecipientApproval.SnapshotId);
            if (!IsLowerSha256(item.RecipientApproval.DestinationSha256))
            {
                throw new InvalidOperationException("The payout destination digest is invalid.");
            }

            try
            {
                total = checked(total + item.AmountCents);
            }
            catch (OverflowException)
            {
                throw new InvalidOperationException("The sandbox payout total is invalid.");
            }
        }

        if (total > 10_000_000)
        {
            throw new InvalidOperationException("The sandbox payout total is invalid.");
        }

        if (preview.CarePeriodId is not null)
        {
            RequireCarePeriodId(preview.CarePeriodId);
        }
    }

    public static void RequireAllocationBoundPreview(PayoutBatchPreview preview)
    {
        RequirePreview(preview);
        if (preview.CarePeriodId is null)
        {
            throw new InvalidOperationException(
                "The sandbox payout preview is not bound to a CARE earning period.");
        }
    }

    public static IReadOnlyList<PayoutItemResult> NormalizeTerminalResult(
        PayoutBatchPreview preview,
        PayoutBatchResult result)
    {
        RequirePreview(preview);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.BatchId, preview.BatchId, StringComparison.Ordinal) ||
            !result.IsTerminal || !IsProviderStatus(result.ProviderStatus))
        {
            throw new InvalidOperationException("The sandbox payout result does not match its approved preview.");
        }

        var supplied = result.ItemResults?.ToArray() ?? [];
        if (supplied.Length == 0 && result.Outcome == PayoutBatchOutcome.Failed)
        {
            supplied = preview.Items.Select(item => new PayoutItemResult(
                item.ContributorPseudonym,
                item.AmountCents,
                false,
                result.ProviderStatus)).ToArray();
        }

        if (supplied.Length != preview.Items.Count)
        {
            throw new InvalidOperationException("The sandbox payout item results are incomplete.");
        }

        var byContributor = new Dictionary<string, PayoutItemResult>(StringComparer.Ordinal);
        foreach (var item in supplied)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.ContributorPseudonym) ||
                !IsProviderStatus(item.ProviderStatus) ||
                !byContributor.TryAdd(item.ContributorPseudonym, item))
            {
                throw new InvalidOperationException("The sandbox payout item result is invalid or duplicated.");
            }
        }

        var normalized = new PayoutItemResult[preview.Items.Count];
        for (var index = 0; index < preview.Items.Count; index++)
        {
            var expected = preview.Items[index];
            if (!byContributor.TryGetValue(expected.ContributorPseudonym, out var actual) ||
                actual.AmountCents != expected.AmountCents)
            {
                throw new InvalidOperationException("The sandbox payout item result conflicts with its approved preview.");
            }

            normalized[index] = actual;
        }

        var succeeded = normalized.Count(item => item.Succeeded);
        var expectedOutcome = succeeded switch
        {
            0 => PayoutBatchOutcome.Failed,
            var count when count == normalized.Length => PayoutBatchOutcome.Succeeded,
            _ => PayoutBatchOutcome.PartiallySucceeded,
        };
        if (result.Outcome != expectedOutcome)
        {
            throw new InvalidOperationException("The sandbox payout outcome conflicts with its item results.");
        }

        return normalized;
    }

    public static void ValidateProviderResult(PayoutBatchPreview preview, PayoutBatchResult result)
    {
        if (result.Outcome == PayoutBatchOutcome.Pending)
        {
            RequirePreview(preview);
            if (!string.Equals(result.BatchId, preview.BatchId, StringComparison.Ordinal) ||
                !IsProviderStatus(result.ProviderStatus) ||
                result.ItemResults is { Count: > 0 })
            {
                throw new InvalidOperationException("The sandbox provider returned an invalid pending result.");
            }

            return;
        }

        _ = NormalizeTerminalResult(preview, result);
    }

    internal static void RequireSnapshotId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not (':' or '.' or '_' or '-')))
        {
            throw new InvalidOperationException("The payout recipient snapshot identifier is invalid.");
        }
    }

    public static void RequireCarePeriodId(string value)
    {
        if (value is not { Length: 12 } ||
            !value.StartsWith("care:", StringComparison.Ordinal) ||
            !int.TryParse(value.AsSpan(5, 4), out var year) || year is < 2000 or > 9999 ||
            value[9] != '-' ||
            !int.TryParse(value.AsSpan(10, 2), out var month) || month is < 1 or > 12 ||
            !string.Equals(value, $"care:{year:D4}-{month:D2}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The CARE earning-period identifier is invalid.");
        }
    }

    internal static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsProviderStatus(string? status) =>
        !string.IsNullOrWhiteSpace(status) && status.Length <= 128 &&
        status.All(character => character is >= ' ' and <= '~');

    private static bool IsIdentifier(string value) =>
        value.All(character => char.IsAsciiLetterOrDigit(character) && !char.IsWhiteSpace(character) ||
            character is ':' or '.' or '_' or '-');
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
        PayoutContractValidation.RequirePreview(preview);
        if (!accountEligibilityVerified || !ownerApproved || today < preview.EligibilityDate)
        {
            throw new InvalidOperationException("The sandbox payout eligibility or owner-approval gate is not satisfied.");
        }

        if (!executedBatches.Add(preview.BatchId))
        {
            throw new InvalidOperationException("The payout batch has already been executed.");
        }

        var result = await sandboxProvider.SendSandboxAsync(preview, cancellationToken).ConfigureAwait(false);
        PayoutContractValidation.ValidateProviderResult(preview, result);
        return result;
    }
}
