using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCx.Core.Contributions;

public sealed record QualifiedUsageReceipt(
    string ReceiptId,
    string InstallationPseudonym,
    string EntitlementId,
    string ResourceId,
    string ResourceVersion,
    string Nonce,
    DateTimeOffset OccurredUtc,
    bool ExecutionSucceeded,
    string VerificationCode);

public sealed record UsageReceiptContext(
    bool EntitlementActive,
    bool ResourceApproved,
    bool IsContributorSelfUse,
    int PriorQualifiedEventsForDay,
    bool ReceiptIdSeen,
    bool NonceSeen,
    DateTimeOffset NowUtc);

public sealed record UsageReceiptDecision(bool Qualified, string Code);

public static class UsageReceiptPolicy
{
    public const int MaximumQualifiedEventsPerResourceDay = 100;

    public static UsageReceiptDecision Evaluate(QualifiedUsageReceipt receipt, UsageReceiptContext context)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(context);
        if (!ValidIdentifier(receipt.ReceiptId, 128) || !ValidIdentifier(receipt.InstallationPseudonym, 128) ||
            !ValidIdentifier(receipt.EntitlementId, 128) || !ValidIdentifier(receipt.ResourceId, 128) ||
            !ValidIdentifier(receipt.ResourceVersion, 64) || !ValidIdentifier(receipt.Nonce, 128) ||
            !ValidIdentifier(receipt.VerificationCode, 64))
        {
            return new UsageReceiptDecision(false, "schema_invalid");
        }

        if (context.ReceiptIdSeen || context.NonceSeen)
        {
            return new UsageReceiptDecision(false, "replay");
        }

        if (!context.EntitlementActive || !context.ResourceApproved)
        {
            return new UsageReceiptDecision(false, "ineligible");
        }

        if (context.IsContributorSelfUse)
        {
            return new UsageReceiptDecision(false, "self_use");
        }

        if (!receipt.ExecutionSucceeded || receipt.VerificationCode != "verified-success")
        {
            return new UsageReceiptDecision(false, "unverified_execution");
        }

        if (receipt.OccurredUtc > context.NowUtc.AddMinutes(5) ||
            receipt.OccurredUtc < context.NowUtc.AddDays(-7))
        {
            return new UsageReceiptDecision(false, "timestamp_out_of_range");
        }

        if (context.PriorQualifiedEventsForDay >= MaximumQualifiedEventsPerResourceDay)
        {
            return new UsageReceiptDecision(false, "rate_anomaly");
        }

        return new UsageReceiptDecision(true, "qualified");
    }

    public static string Sign(QualifiedUsageReceipt receipt, ReadOnlySpan<byte> installationKey)
    {
        if (installationKey.Length < 32)
        {
            throw new ArgumentException("The receipt integrity key is too short.", nameof(installationKey));
        }

        return Convert.ToBase64String(HMACSHA256.HashData(installationKey, CanonicalBytes(receipt)));
    }

    public static bool Verify(QualifiedUsageReceipt receipt, string signatureBase64, ReadOnlySpan<byte> installationKey)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(Sign(receipt, installationKey)),
                Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] CanonicalBytes(QualifiedUsageReceipt receipt) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            receipt.ReceiptId,
            receipt.InstallationPseudonym,
            receipt.EntitlementId,
            receipt.ResourceId,
            receipt.ResourceVersion,
            receipt.Nonce,
            occurredUtc = receipt.OccurredUtc.ToUniversalTime().ToString("O"),
            receipt.ExecutionSucceeded,
            receipt.VerificationCode,
        });

    private static bool ValidIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/');
}
