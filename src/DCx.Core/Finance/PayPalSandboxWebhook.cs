using System.Security.Cryptography;
using System.Text.Json;

namespace DCx.Core.Finance;

public sealed record PayPalSandboxWebhookEvent(
    string EventId,
    string TransmissionId,
    string EventType,
    string SubscriptionId,
    long AmountCents,
    string Currency,
    DateTimeOffset OccurredUtc);

public sealed record SubscriptionEntitlement(string SubscriptionId, bool Active, DateTimeOffset UpdatedUtc);

public sealed record SubscriptionEntitlementTransition(
    bool Applies,
    bool Active,
    string DecisionCode);

public static class PayPalSandboxWebhookPolicy
{
    public const string EntitlementAppliedInitial = "entitlement_applied_initial";
    public const string EntitlementAppliedNewer = "entitlement_applied_newer";
    public const string EntitlementAppliedEqualDeactivation = "entitlement_applied_equal_deactivation";
    public const string EntitlementStaleIgnored = "entitlement_stale_ignored";
    public const string EntitlementEqualIgnored = "entitlement_equal_ignored";
    public const string LedgerOnly = "ledger_only";

    public static void Validate(PayPalSandboxWebhookEvent webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!IsIdentifier(webhook.EventId) || !IsIdentifier(webhook.TransmissionId) ||
            !IsIdentifier(webhook.SubscriptionId) ||
            !string.Equals(webhook.Currency, "USD", StringComparison.Ordinal) ||
            webhook.OccurredUtc == default ||
            webhook.EventType is not ("BILLING.SUBSCRIPTION.ACTIVATED" or
                "BILLING.SUBSCRIPTION.CANCELLED" or "BILLING.SUBSCRIPTION.SUSPENDED" or
                "PAYMENT.SALE.COMPLETED" or "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED"))
        {
            throw new InvalidOperationException(
                "The sandbox webhook schema, event type, currency, or time is invalid.");
        }

        if ((webhook.EventType.StartsWith("BILLING.", StringComparison.Ordinal) &&
             webhook.AmountCents != 0) ||
            (webhook.EventType == "PAYMENT.SALE.COMPLETED" &&
             webhook.AmountCents != PayPalSandboxWebhookProcessor.MonthlyPlanCents) ||
            (webhook.EventType is "PAYMENT.SALE.REFUNDED" or "PAYMENT.SALE.REVERSED" &&
             webhook.AmountCents is < 1 or > PayPalSandboxWebhookProcessor.MonthlyPlanCents))
        {
            throw new InvalidOperationException("The sandbox webhook amount does not match the US$3 plan.");
        }
    }

    public static bool TryGetEntitlementTarget(string eventType, out bool active)
    {
        active = string.Equals(
            eventType,
            "BILLING.SUBSCRIPTION.ACTIVATED",
            StringComparison.Ordinal);
        return active || eventType is "BILLING.SUBSCRIPTION.CANCELLED" or
            "BILLING.SUBSCRIPTION.SUSPENDED" or "PAYMENT.SALE.REFUNDED" or
            "PAYMENT.SALE.REVERSED";
    }

    public static SubscriptionEntitlementTransition ReduceEntitlement(
        SubscriptionEntitlement? current,
        bool targetActive,
        DateTimeOffset occurredUtc)
    {
        var canonicalTime = occurredUtc.ToUniversalTime();
        if (current is null)
        {
            return new SubscriptionEntitlementTransition(
                true, targetActive, EntitlementAppliedInitial);
        }

        var comparison = canonicalTime.CompareTo(current.UpdatedUtc.ToUniversalTime());
        if (comparison > 0)
        {
            return new SubscriptionEntitlementTransition(
                true, targetActive, EntitlementAppliedNewer);
        }

        if (comparison < 0)
        {
            return new SubscriptionEntitlementTransition(
                false, current.Active, EntitlementStaleIgnored);
        }

        if (current.Active && !targetActive)
        {
            return new SubscriptionEntitlementTransition(
                true, false, EntitlementAppliedEqualDeactivation);
        }

        return new SubscriptionEntitlementTransition(
            false, current.Active, EntitlementEqualIgnored);
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
            character is ':' or '.' or '_' or '-');
}

public static class PayPalSandboxWebhookIntegrity
{
    public static string Sign(PayPalSandboxWebhookEvent webhook, ReadOnlySpan<byte> verificationKey)
    {
        if (verificationKey.Length < 32)
        {
            throw new ArgumentException("The sandbox webhook key is too short.", nameof(verificationKey));
        }

        return Convert.ToBase64String(HMACSHA256.HashData(verificationKey, CanonicalBytes(webhook)));
    }

    public static bool Verify(
        PayPalSandboxWebhookEvent webhook,
        string signatureBase64,
        ReadOnlySpan<byte> verificationKey)
    {
        if (verificationKey.Length < 32)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                HMACSHA256.HashData(verificationKey, CanonicalBytes(webhook)),
                Convert.FromBase64String(signatureBase64));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentNullException)
        {
            return false;
        }
    }

    public static byte[] CanonicalBytes(PayPalSandboxWebhookEvent webhook) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            webhook.EventId,
            webhook.TransmissionId,
            webhook.EventType,
            webhook.SubscriptionId,
            webhook.AmountCents,
            webhook.Currency,
            occurredUtc = webhook.OccurredUtc.ToUniversalTime().ToString("O"),
        });
}

public sealed class PayPalSandboxWebhookProcessor(DoubleEntryLedger ledger, byte[] verificationKey)
{
    public const long MonthlyPlanCents = 300;

    private readonly Dictionary<string, SubscriptionEntitlement> entitlements = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> transmissionPayloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> eventPayloads = new(StringComparer.Ordinal);

    public string Sign(PayPalSandboxWebhookEvent webhook) =>
        PayPalSandboxWebhookIntegrity.Sign(webhook, verificationKey);

    public bool Process(PayPalSandboxWebhookEvent webhook, string signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (!PayPalSandboxWebhookIntegrity.Verify(webhook, signatureBase64, verificationKey))
        {
            throw new InvalidOperationException("The sandbox webhook signature is invalid.");
        }

        PayPalSandboxWebhookPolicy.Validate(webhook);
        var payloadSha256 = Convert.ToHexStringLower(
            SHA256.HashData(PayPalSandboxWebhookIntegrity.CanonicalBytes(webhook)));
        var eventExists = eventPayloads.TryGetValue(webhook.EventId, out var eventPayload);
        var transmissionExists = transmissionPayloads.TryGetValue(
            webhook.TransmissionId, out var transmissionPayload);
        if (eventExists || transmissionExists)
        {
            if (eventExists && transmissionExists &&
                string.Equals(eventPayload, payloadSha256, StringComparison.Ordinal) &&
                string.Equals(transmissionPayload, payloadSha256, StringComparison.Ordinal))
            {
                return false;
            }

            throw new InvalidOperationException(
                "A sandbox webhook event or transmission identifier conflicts with prior data.");
        }

        switch (webhook.EventType)
        {
            case "BILLING.SUBSCRIPTION.ACTIVATED":
            case "BILLING.SUBSCRIPTION.CANCELLED":
            case "BILLING.SUBSCRIPTION.SUSPENDED":
                ApplyEntitlement(webhook);
                break;
            case "PAYMENT.SALE.COMPLETED":
                ledger.Post(Entry(webhook, "subscription_payment",
                    new LedgerPosting(LedgerAccount.Cash, webhook.AmountCents, 0),
                    new LedgerPosting(LedgerAccount.SubscriptionRevenue, 0, webhook.AmountCents)));
                break;
            case "PAYMENT.SALE.REFUNDED":
            case "PAYMENT.SALE.REVERSED":
                ledger.Post(Entry(webhook, "refund_or_chargeback",
                    new LedgerPosting(LedgerAccount.RefundsAndChargebacks, webhook.AmountCents, 0),
                    new LedgerPosting(LedgerAccount.Cash, 0, webhook.AmountCents)));
                ApplyEntitlement(webhook);
                break;
            default:
                throw new InvalidOperationException("The sandbox webhook event type is unsupported.");
        }

        eventPayloads.Add(webhook.EventId, payloadSha256);
        transmissionPayloads.Add(webhook.TransmissionId, payloadSha256);
        return true;
    }

    public SubscriptionEntitlement? GetEntitlement(string subscriptionId) =>
        entitlements.GetValueOrDefault(subscriptionId);

    private static LedgerJournalEntry Entry(
        PayPalSandboxWebhookEvent webhook,
        string description,
        params LedgerPosting[] postings) =>
        new(webhook.EventId, webhook.OccurredUtc, description, postings);

    private void ApplyEntitlement(PayPalSandboxWebhookEvent webhook)
    {
        _ = PayPalSandboxWebhookPolicy.TryGetEntitlementTarget(webhook.EventType, out var active);
        var current = entitlements.GetValueOrDefault(webhook.SubscriptionId);
        var transition = PayPalSandboxWebhookPolicy.ReduceEntitlement(
            current, active, webhook.OccurredUtc);
        if (transition.Applies)
        {
            entitlements[webhook.SubscriptionId] = new SubscriptionEntitlement(
                webhook.SubscriptionId,
                transition.Active,
                webhook.OccurredUtc.ToUniversalTime());
        }
    }

}
