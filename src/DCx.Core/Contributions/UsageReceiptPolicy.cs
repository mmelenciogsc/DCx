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
    string AuthoritativeInstallationPseudonym,
    string AuthoritativeEntitlementId,
    UsageReceiptActivity Activity,
    bool ReceiptIdSeen,
    bool NonceSeen,
    DateTimeOffset NowUtc);

public sealed record UsageReceiptActivity(
    int PriorQualifiedEventsForResourceDay,
    int PriorQualifiedEventsForInstallationMinute,
    int PriorQualifiedEventsForInstallationDay,
    int PriorQualifiedEventsForEntitlementDay,
    int PriorDistinctResourcesForInstallationDay,
    bool ResourceSeenForInstallationDay);

public sealed record UsageReceiptDecision(bool Qualified, string Code);

public static class UsageReceiptPolicy
{
    public const int MaximumQualifiedEventsPerResourceDay = 100;
    public const int MaximumQualifiedEventsPerInstallationMinute = 120;
    public const int MaximumQualifiedEventsPerInstallationDay = 2_000;
    public const int MaximumQualifiedEventsPerEntitlementDay = 10_000;
    public const int MaximumDistinctResourcesPerInstallationDay = 128;

    public static UsageReceiptDecision Evaluate(QualifiedUsageReceipt receipt, UsageReceiptContext context)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(context);
        if (!HasValidSchema(receipt))
        {
            return new UsageReceiptDecision(false, "schema_invalid");
        }

        if (!ValidIdentifier(context.AuthoritativeInstallationPseudonym, 128) ||
            !ValidIdentifier(context.AuthoritativeEntitlementId, 128) ||
            context.Activity is null || !HasValidActivity(context.Activity) ||
            context.NowUtc.Offset != TimeSpan.Zero)
        {
            return new UsageReceiptDecision(false, "context_invalid");
        }

        if (context.ReceiptIdSeen || context.NonceSeen)
        {
            return new UsageReceiptDecision(false, "replay");
        }

        if (!string.Equals(
                receipt.InstallationPseudonym,
                context.AuthoritativeInstallationPseudonym,
                StringComparison.Ordinal) ||
            !string.Equals(receipt.EntitlementId, context.AuthoritativeEntitlementId, StringComparison.Ordinal))
        {
            return new UsageReceiptDecision(false, "identity_mismatch");
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

        if (context.Activity.PriorQualifiedEventsForResourceDay >= MaximumQualifiedEventsPerResourceDay ||
            context.Activity.PriorQualifiedEventsForInstallationMinute >= MaximumQualifiedEventsPerInstallationMinute ||
            context.Activity.PriorQualifiedEventsForInstallationDay >= MaximumQualifiedEventsPerInstallationDay ||
            context.Activity.PriorQualifiedEventsForEntitlementDay >= MaximumQualifiedEventsPerEntitlementDay ||
            (!context.Activity.ResourceSeenForInstallationDay &&
             context.Activity.PriorDistinctResourcesForInstallationDay >= MaximumDistinctResourcesPerInstallationDay))
        {
            return new UsageReceiptDecision(false, "rate_anomaly");
        }

        return new UsageReceiptDecision(true, "qualified");
    }

    public static string Sign(QualifiedUsageReceipt receipt, ReadOnlySpan<byte> installationKey)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (installationKey.Length < 32)
        {
            throw new ArgumentException("The receipt integrity key is too short.", nameof(installationKey));
        }

        if (!HasValidSchema(receipt))
        {
            throw new ArgumentException("The usage receipt schema is invalid.", nameof(receipt));
        }

        return Convert.ToBase64String(HMACSHA256.HashData(installationKey, CanonicalBytes(receipt)));
    }

    public static bool Verify(QualifiedUsageReceipt receipt, string signatureBase64, ReadOnlySpan<byte> installationKey)
    {
        if (receipt is null || installationKey.Length < 32 ||
            string.IsNullOrWhiteSpace(signatureBase64) || signatureBase64.Length > 128 ||
            !HasValidSchema(receipt))
        {
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(installationKey, CanonicalBytes(receipt));
            var actual = Convert.FromBase64String(signatureBase64);
            return CryptographicOperations.FixedTimeEquals(
                expected,
                actual);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
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

    private static bool HasValidActivity(UsageReceiptActivity activity) =>
        activity.PriorQualifiedEventsForResourceDay >= 0 &&
        activity.PriorQualifiedEventsForInstallationMinute >= 0 &&
        activity.PriorQualifiedEventsForInstallationDay >= 0 &&
        activity.PriorQualifiedEventsForEntitlementDay >= 0 &&
        activity.PriorDistinctResourcesForInstallationDay >= 0 &&
        activity.PriorDistinctResourcesForInstallationDay <= activity.PriorQualifiedEventsForInstallationDay;

    private static bool HasValidSchema(QualifiedUsageReceipt receipt) =>
        ValidIdentifier(receipt.ReceiptId, 128) && ValidIdentifier(receipt.InstallationPseudonym, 128) &&
        ValidIdentifier(receipt.EntitlementId, 128) && ValidIdentifier(receipt.ResourceId, 128) &&
        ValidIdentifier(receipt.ResourceVersion, 64) && ValidIdentifier(receipt.Nonce, 128) &&
        ValidIdentifier(receipt.VerificationCode, 64);
}
