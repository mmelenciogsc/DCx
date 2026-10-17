namespace DCx.Core.Finance;

public sealed record CarePeriodInput(
    int Year,
    int Month,
    long SubscriptionRevenueCents,
    long RefundAndChargebackCents,
    long PaymentFeeCents,
    long ApprovedOperatingCostCents,
    long TaxAndRiskReserveCents,
    int ContributorPoolBasisPoints = 4000,
    long MinimumPayoutCents = 1000);

public sealed record ContributorUsageWeight(string ContributorPseudonym, decimal Weight);

public sealed record ContributorAllocation(
    string ContributorPseudonym,
    long AmountCents,
    bool HeldBelowThreshold);

public sealed record CarePeriodResult(
    int Year,
    int Month,
    long DistributableNetProfitCents,
    long AggregateContributorPoolCents,
    DateOnly EarningPeriodClose,
    DateOnly Net60EligibilityDate,
    IReadOnlyList<ContributorAllocation> Allocations);

public static class CareAllocationCalculator
{
    public static CarePeriodResult Calculate(
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(usage);
        if (input.Year is < 2000 or > 9999 || input.Month is < 1 or > 12 ||
            input.ContributorPoolBasisPoints is < 0 or > 4000 || input.MinimumPayoutCents < 0 ||
            input.SubscriptionRevenueCents < 0 || input.RefundAndChargebackCents < 0 ||
            input.PaymentFeeCents < 0 || input.ApprovedOperatingCostCents < 0 || input.TaxAndRiskReserveCents < 0 ||
            usage.Any(item => string.IsNullOrWhiteSpace(item.ContributorPseudonym) || item.Weight <= 0) ||
            usage.Select(item => item.ContributorPseudonym).Distinct(StringComparer.Ordinal).Count() != usage.Count)
        {
            throw new ArgumentException("The CARE period input or usage weights are invalid.");
        }

        var deductions = checked(input.RefundAndChargebackCents + input.PaymentFeeCents +
                                 input.ApprovedOperatingCostCents + input.TaxAndRiskReserveCents);
        var net = Math.Max(0, input.SubscriptionRevenueCents - deductions);
        var pool = net == 0 ? 0 : checked(net * input.ContributorPoolBasisPoints / 10_000);
        var allocations = Allocate(pool, input.MinimumPayoutCents, usage);
        var close = new DateOnly(input.Year, input.Month, DateTime.DaysInMonth(input.Year, input.Month));
        return new CarePeriodResult(
            input.Year,
            input.Month,
            net,
            pool,
            close,
            close.AddDays(60),
            allocations);
    }

    private static IReadOnlyList<ContributorAllocation> Allocate(
        long pool,
        long minimumPayout,
        IReadOnlyList<ContributorUsageWeight> usage)
    {
        if (pool == 0 || usage.Count == 0)
        {
            return [];
        }

        var totalWeight = usage.Sum(item => item.Weight);
        var shares = usage.Select(item =>
        {
            var exact = pool * item.Weight / totalWeight;
            var floor = decimal.ToInt64(decimal.Floor(exact));
            return new { item.ContributorPseudonym, Floor = floor, Remainder = exact - floor };
        }).ToArray();
        var remaining = pool - shares.Sum(item => item.Floor);
        var bonus = shares
            .OrderByDescending(item => item.Remainder)
            .ThenBy(item => item.ContributorPseudonym, StringComparer.Ordinal)
            .Take((int)remaining)
            .Select(item => item.ContributorPseudonym)
            .ToHashSet(StringComparer.Ordinal);
        return shares
            .OrderBy(item => item.ContributorPseudonym, StringComparer.Ordinal)
            .Select(item =>
            {
                var amount = item.Floor + (bonus.Contains(item.ContributorPseudonym) ? 1 : 0);
                return new ContributorAllocation(item.ContributorPseudonym, amount, amount < minimumPayout);
            })
            .ToArray();
    }
}
