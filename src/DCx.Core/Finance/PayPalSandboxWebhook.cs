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

public sealed class PayPalSandboxWebhookProcessor(DoubleEntryLedger ledger, byte[] verificationKey)
{
    public const long MonthlyPlanCents = 300;

    private readonly Dictionary<string, SubscriptionEntitlement> entitlements = new(StringComparer.Ordinal);
    private readonly HashSet<string> transmissionIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> eventIds = new(StringComparer.Ordinal);

    public string Sign(PayPalSandboxWebhookEvent webhook) =>
        Convert.ToBase64String(HMACSHA256.HashData(verificationKey, CanonicalBytes(webhook)));

    public bool Process(PayPalSandboxWebhookEvent webhook, string signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        if (verificationKey.Length < 32 || !Verify(webhook, signatureBase64))
        {
            throw new InvalidOperationException("The sandbox webhook signature is invalid.");
        }

        if (transmissionIds.Contains(webhook.TransmissionId) || eventIds.Contains(webhook.EventId))
        {
            return false;
        }

        if (!string.Equals(webhook.Currency, "USD", StringComparison.Ordinal) || webhook.AmountCents < 0)
        {
            throw new InvalidOperationException("The sandbox webhook currency or amount is invalid.");
        }

        transmissionIds.Add(webhook.TransmissionId);
        eventIds.Add(webhook.EventId);

        switch (webhook.EventType)
        {
            case "BILLING.SUBSCRIPTION.ACTIVATED":
                entitlements[webhook.SubscriptionId] = new SubscriptionEntitlement(webhook.SubscriptionId, true, webhook.OccurredUtc);
                break;
            case "BILLING.SUBSCRIPTION.CANCELLED":
            case "BILLING.SUBSCRIPTION.SUSPENDED":
                entitlements[webhook.SubscriptionId] = new SubscriptionEntitlement(webhook.SubscriptionId, false, webhook.OccurredUtc);
                break;
            case "PAYMENT.SALE.COMPLETED":
                if (webhook.AmountCents != MonthlyPlanCents)
                {
                    throw new InvalidOperationException("The sandbox payment does not match the US$3 monthly plan.");
                }

                ledger.Post(Entry(webhook, "subscription_payment",
                    new LedgerPosting(LedgerAccount.Cash, webhook.AmountCents, 0),
                    new LedgerPosting(LedgerAccount.SubscriptionRevenue, 0, webhook.AmountCents)));
                break;
            case "PAYMENT.SALE.REFUNDED":
            case "PAYMENT.SALE.REVERSED":
                ledger.Post(Entry(webhook, "refund_or_chargeback",
                    new LedgerPosting(LedgerAccount.RefundsAndChargebacks, webhook.AmountCents, 0),
                    new LedgerPosting(LedgerAccount.Cash, 0, webhook.AmountCents)));
                entitlements[webhook.SubscriptionId] = new SubscriptionEntitlement(webhook.SubscriptionId, false, webhook.OccurredUtc);
                break;
            default:
                throw new InvalidOperationException("The sandbox webhook event type is unsupported.");
        }

        return true;
    }

    public SubscriptionEntitlement? GetEntitlement(string subscriptionId) =>
        entitlements.GetValueOrDefault(subscriptionId);

    private bool Verify(PayPalSandboxWebhookEvent webhook, string signatureBase64)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                HMACSHA256.HashData(verificationKey, CanonicalBytes(webhook)),
                Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static LedgerJournalEntry Entry(
        PayPalSandboxWebhookEvent webhook,
        string description,
        params LedgerPosting[] postings) =>
        new(webhook.EventId, webhook.OccurredUtc, description, postings);

    private static byte[] CanonicalBytes(PayPalSandboxWebhookEvent webhook) =>
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
