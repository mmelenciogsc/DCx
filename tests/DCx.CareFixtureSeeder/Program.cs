using System.Text.Json;
using DCx.Core.Finance;
using DCx.GlobalService;

if (args.Length != 1)
{
    throw new InvalidOperationException(
        "The CARE fixture seeder requires one empty validation data root.");
}

var dataRoot = Path.GetFullPath(args[0]);
if (Directory.Exists(dataRoot) && Directory.EnumerateFileSystemEntries(dataRoot).Any())
{
    throw new InvalidOperationException(
        "The CARE fixture seeder refuses a nonempty validation data root.");
}

Directory.CreateDirectory(dataRoot);
var repository = await DurableCareRepository.OpenAsync(Path.Combine(dataRoot, "care"));
var now = DateTimeOffset.Parse("2026-10-18T01:00:00Z");
var usage = new[]
{
    new ContributorUsageWeight("contributor:fixture-one", 500),
    new ContributorUsageWeight("contributor:fixture-two", 700),
};
var closeReview = DurableCareRepository.ReviewCarePeriodClose(
    new CarePeriodInput(
        2026,
        5,
        SubscriptionRevenueCents: 3000,
        RefundAndChargebackCents: 0,
        PaymentFeeCents: 0,
        ApprovedOperatingCostCents: 0,
        TaxAndRiskReserveCents: 0,
        ContributorPoolBasisPoints: 4000,
        MinimumPayoutCents: 0),
    usage,
    now);
var period = await repository.RegisterReviewedCarePeriodAsync(
    closeReview,
    ownerApproved: true,
    now);
var preview = new PayoutBatchPreview(
    "batch:loopback-uia-fixture",
    PayoutExecutionMode.Sandbox,
    period.Net60EligibilityDate,
    [
        new PayoutItem(
            usage[0].ContributorPseudonym,
            decimal.ToInt64(usage[0].Weight),
            PayoutRecipientApproval.Create(
                "snapshot:loopback-uia-one",
                "loopback-uia-one@personal.example.com")),
        new PayoutItem(
            usage[1].ContributorPseudonym,
            decimal.ToInt64(usage[1].Weight),
            PayoutRecipientApproval.Create(
                "snapshot:loopback-uia-two",
                "loopback-uia-two@personal.example.com")),
    ],
    PayoutBatchPreviewFactory.CarePeriodId(period));
if (!await repository.ReservePayoutAsync(
        preview,
        accountEligibilityVerified: true,
        ownerApproved: true,
        today: preview.EligibilityDate,
        now))
{
    throw new InvalidOperationException("The synthetic payout reservation was not created.");
}

if (!await repository.CompletePayoutAsync(
        preview,
        new PayoutBatchResult(
            preview.BatchId,
            PayoutBatchOutcome.PartiallySucceeded,
            "SUCCESS",
            [
                new PayoutItemResult(
                    usage[0].ContributorPseudonym,
                    decimal.ToInt64(usage[0].Weight),
                    Succeeded: true,
                    ProviderStatus: "SUCCESS"),
                new PayoutItemResult(
                    usage[1].ContributorPseudonym,
                    decimal.ToInt64(usage[1].Weight),
                    Succeeded: false,
                    ProviderStatus: "FAILED"),
            ]),
        now))
{
    throw new InvalidOperationException("The synthetic payout result was not recorded.");
}

var review = (await repository.ListFailedPayoutReviewsAsync()).Single();
Console.WriteLine(JsonSerializer.Serialize(new
{
    schemaVersion = 1,
    review.ReviewId,
    review.CarePeriodId,
    review.AmountCents,
    review.FailureCode,
    review.HoldCode,
    review.NoticeCode,
    review.DispositionCode,
    providerRequests = 0,
    payoutAttempts = 0,
    notificationTransports = 0,
    productionActions = 0,
}));
