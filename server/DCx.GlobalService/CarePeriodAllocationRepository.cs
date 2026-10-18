using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Finance;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed record CarePeriodCloseReview(
    CarePeriodInput Input,
    IReadOnlyList<ContributorUsageWeight> Usage,
    CarePeriodResult Result,
    DateTimeOffset ReviewedUtc,
    DateTimeOffset ExpiresUtc,
    string ReviewSha256);

public sealed partial class DurableCareRepository
{
    private const int MaximumCareContributorsPerPeriod = 100_000;
    private static readonly TimeSpan CarePeriodCloseReviewLifetime = TimeSpan.FromMinutes(10);

    public static CarePeriodCloseReview ReviewCarePeriodClose(
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        var canonicalUsage = CanonicalizeCareUsage(usage);
        var result = CareAllocationCalculator.Calculate(input, canonicalUsage);
        var reviewedUtc = now.ToUniversalTime();
        var expiresUtc = reviewedUtc.Add(CarePeriodCloseReviewLifetime);
        return new CarePeriodCloseReview(
            input,
            canonicalUsage,
            result,
            reviewedUtc,
            expiresUtc,
            CarePeriodCloseReviewSha(
                input, canonicalUsage, result, reviewedUtc, expiresUtc));
    }

    public async Task<CarePeriodResult> RegisterReviewedCarePeriodAsync(
        CarePeriodCloseReview review,
        bool ownerApproved,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!ownerApproved)
        {
            throw new InvalidOperationException(
                "CARE period close requires explicit owner approval.");
        }

        var approvedUtc = now.ToUniversalTime();
        var canonicalUsage = CanonicalizeCareUsage(review.Usage);
        var result = CareAllocationCalculator.Calculate(review.Input, canonicalUsage);
        if (review.ReviewedUtc.Offset != TimeSpan.Zero ||
            review.ExpiresUtc.Offset != TimeSpan.Zero ||
            review.ExpiresUtc - review.ReviewedUtc != CarePeriodCloseReviewLifetime ||
            approvedUtc < review.ReviewedUtc || approvedUtc > review.ExpiresUtc ||
            !CarePeriodResultsEqual(review.Result, result) ||
            !PayoutContractValidation.IsLowerSha256(review.ReviewSha256))
        {
            throw new InvalidOperationException(
                "The CARE period close review is invalid, stale or expired.");
        }

        var expectedReviewSha = CarePeriodCloseReviewSha(
            review.Input,
            canonicalUsage,
            result,
            review.ReviewedUtc,
            review.ExpiresUtc);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedReviewSha),
                Convert.FromHexString(review.ReviewSha256)))
        {
            throw new InvalidOperationException(
                "The CARE period close review digest is invalid.");
        }
        var approvalSha = CarePeriodCloseApprovalSha(
            review.ReviewSha256,
            review.ReviewedUtc,
            review.ExpiresUtc,
            approvedUtc);

        return await RegisterCarePeriodInternalAsync(
                review.Input,
                canonicalUsage,
                result,
                review.ReviewedUtc,
                review.ExpiresUtc,
                review.ReviewSha256,
                approvalSha,
                approvedUtc,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CarePeriodResult> RegisterCarePeriodInternalAsync(
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> canonicalUsage,
        CarePeriodResult result,
        DateTimeOffset reviewedUtc,
        DateTimeOffset reviewExpiresUtc,
        string closeReviewSha256,
        string closeApprovalSha256,
        DateTimeOffset approvedUtc,
        CancellationToken cancellationToken)
    {
        var periodId = PayoutBatchPreviewFactory.CarePeriodId(result);
        var registrationSha = CarePeriodRegistrationSha(input, canonicalUsage, result);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            var existing = await ReadCarePeriodRegistrationAsync(
                    connection, transaction, periodId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                ValidateStoredCarePeriod(existing, input, result, registrationSha);
                await ValidateStoredCareAllocationsAsync(
                        connection, transaction, periodId, canonicalUsage, result, cancellationToken)
                    .ConfigureAwait(false);
                await BindOrValidateStoredCarePeriodCloseReviewAsync(
                        connection,
                        transaction,
                        periodId,
                        existing,
                        reviewedUtc,
                        reviewExpiresUtc,
                        closeReviewSha256,
                        closeApprovalSha256,
                        approvedUtc,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.AggregateContributorPoolCents > 0)
                {
                    var insertedMissingAccrual = await PostInternalAsync(
                            connection,
                            transaction,
                            CareLedgerJournal.ContributorAccrual(
                                periodId,
                                result,
                                DateTimeOffset.Parse(
                                    existing.RegisteredUtc,
                                    CultureInfo.InvariantCulture)),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (insertedMissingAccrual)
                    {
                        throw new InvalidOperationException(
                            "The registered CARE period is missing its durable ledger accrual.");
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }

            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO care_periods(
                    period_id, earning_year, earning_month,
                    subscription_revenue_cents, refund_chargeback_cents,
                    payment_fee_cents, approved_operating_cost_cents,
                    tax_risk_reserve_cents, contributor_pool_basis_points,
                    minimum_payout_cents, distributable_net_profit_cents,
                    contributor_pool_cents, period_close, eligibility_date,
                    registration_sha256, registered_utc,
                    close_review_sha256, close_approval_sha256,
                    reviewed_utc, review_expires_utc, approved_utc)
                VALUES(
                    $period, $year, $month,
                    $revenue, $refunds, $fees, $operating, $reserve, $basis,
                    $minimum, $net, $pool, $close, $eligibility, $sha, $registered,
                    $review_sha, $approval_sha, $reviewed, $review_expires, $approved);
                """;
            command.Parameters.AddWithValue("$period", periodId);
            command.Parameters.AddWithValue("$year", input.Year);
            command.Parameters.AddWithValue("$month", input.Month);
            command.Parameters.AddWithValue("$revenue", input.SubscriptionRevenueCents);
            command.Parameters.AddWithValue("$refunds", input.RefundAndChargebackCents);
            command.Parameters.AddWithValue("$fees", input.PaymentFeeCents);
            command.Parameters.AddWithValue("$operating", input.ApprovedOperatingCostCents);
            command.Parameters.AddWithValue("$reserve", input.TaxAndRiskReserveCents);
            command.Parameters.AddWithValue("$basis", input.ContributorPoolBasisPoints);
            command.Parameters.AddWithValue("$minimum", input.MinimumPayoutCents);
            command.Parameters.AddWithValue("$net", result.DistributableNetProfitCents);
            command.Parameters.AddWithValue("$pool", result.AggregateContributorPoolCents);
            command.Parameters.AddWithValue("$close", result.EarningPeriodClose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$eligibility", result.Net60EligibilityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$sha", registrationSha);
            command.Parameters.AddWithValue("$registered", approvedUtc.ToString("O"));
            command.Parameters.AddWithValue("$review_sha", closeReviewSha256);
            command.Parameters.AddWithValue("$approval_sha", closeApprovalSha256);
            command.Parameters.AddWithValue("$reviewed", reviewedUtc.ToString("O"));
            command.Parameters.AddWithValue("$review_expires", reviewExpiresUtc.ToString("O"));
            command.Parameters.AddWithValue("$approved", approvedUtc.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var allocations = result.Allocations.ToDictionary(
                item => item.ContributorPseudonym,
                StringComparer.Ordinal);
            foreach (var item in canonicalUsage)
            {
                allocations.TryGetValue(item.ContributorPseudonym, out var allocation);
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO care_contributor_allocations(
                        period_id, contributor_pseudonym, qualified_weight,
                        allocation_cents, held_below_threshold)
                    VALUES($period, $contributor, $weight, $amount, $held);
                    """;
                command.Parameters.AddWithValue("$period", periodId);
                command.Parameters.AddWithValue("$contributor", item.ContributorPseudonym);
                command.Parameters.AddWithValue(
                    "$weight", item.Weight.ToString("G29", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$amount", allocation?.AmountCents ?? 0);
                command.Parameters.AddWithValue("$held", allocation?.HeldBelowThreshold is false ? 0 : 1);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (result.AggregateContributorPoolCents > 0)
            {
                _ = await PostInternalAsync(
                        connection,
                        transaction,
                        CareLedgerJournal.ContributorAccrual(periodId, result, approvedUtc),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    private static async Task ValidatePayoutAllocationAvailabilityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        PayoutContractValidation.RequireAllocationBoundPreview(preview);
        var periodId = preview.CarePeriodId!;
        var registeredPeriod = await ValidateRegisteredCarePeriodIntegrityAsync(
                connection, transaction, periodId, cancellationToken)
            .ConfigureAwait(false);
        if (registeredPeriod.Net60EligibilityDate != preview.EligibilityDate)
        {
            throw new InvalidOperationException(
                "The payout preview does not match a registered CARE period and eligibility date.");
        }

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var item in preview.Items)
        {
            command.Parameters.Clear();
            command.CommandText = """
                SELECT allocation_cents, held_below_threshold
                FROM care_contributor_allocations
                WHERE period_id = $period AND contributor_pseudonym = $contributor;
                """;
            command.Parameters.AddWithValue("$period", periodId);
            command.Parameters.AddWithValue("$contributor", item.ContributorPseudonym);
            await using var allocationReader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await allocationReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "The payout item has no registered CARE allocation.");
            }

            var allocationCents = allocationReader.GetInt64(0);
            var held = allocationReader.GetInt32(1) == 1;
            await allocationReader.DisposeAsync().ConfigureAwait(false);
            if (held || allocationCents <= 0)
            {
                throw new InvalidOperationException(
                    "The contributor CARE allocation is held or not payable.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                SELECT COALESCE(SUM(
                    CASE
                        WHEN batch.status = 'reserved' THEN binding.amount_cents
                        WHEN item_result.succeeded = 1 AND adjustment.batch_id IS NULL
                            THEN binding.amount_cents
                        ELSE 0
                    END), 0)
                FROM payout_allocation_bindings AS binding
                INNER JOIN payout_batches AS batch ON batch.batch_id = binding.batch_id
                LEFT JOIN payout_item_results AS item_result
                  ON item_result.batch_id = binding.batch_id
                 AND item_result.ordinal = binding.ordinal
                LEFT JOIN payout_item_adjustments AS adjustment
                  ON adjustment.batch_id = binding.batch_id
                 AND adjustment.ordinal = binding.ordinal
                WHERE binding.period_id = $period
                  AND binding.contributor_pseudonym = $contributor;
                """;
            command.Parameters.AddWithValue("$period", periodId);
            command.Parameters.AddWithValue("$contributor", item.ContributorPseudonym);
            var consumed = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (consumed < 0 || consumed > allocationCents ||
                item.AmountCents > allocationCents - consumed)
            {
                throw new InvalidOperationException(
                    "The payout item exceeds its unreserved CARE allocation.");
            }
        }
    }

    private static async Task InsertPayoutAllocationBindingsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        for (var ordinal = 0; ordinal < preview.Items.Count; ordinal++)
        {
            var item = preview.Items[ordinal];
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO payout_allocation_bindings(
                    batch_id, ordinal, period_id, contributor_pseudonym, amount_cents)
                VALUES($batch, $ordinal, $period, $contributor, $amount);
                """;
            command.Parameters.AddWithValue("$batch", preview.BatchId);
            command.Parameters.AddWithValue("$ordinal", ordinal);
            command.Parameters.AddWithValue("$period", preview.CarePeriodId!);
            command.Parameters.AddWithValue("$contributor", item.ContributorPseudonym);
            command.Parameters.AddWithValue("$amount", item.AmountCents);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidatePayoutAllocationBindingsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        PayoutBatchPreview preview,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ordinal, period_id, contributor_pseudonym, amount_cents
            FROM payout_allocation_bindings
            WHERE batch_id = $batch
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$batch", preview.BatchId);
        var bindings = new List<(int Ordinal, string PeriodId, string Contributor, long Amount)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                bindings.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            }
        }

        if (preview.CarePeriodId is null)
        {
            if (bindings.Count != 0)
            {
                throw new InvalidOperationException(
                    "A legacy payout has unexpected CARE allocation bindings.");
            }

            return;
        }

        if (bindings.Count != preview.Items.Count)
        {
            throw new InvalidOperationException("The payout CARE allocation binding is incomplete.");
        }

        for (var ordinal = 0; ordinal < bindings.Count; ordinal++)
        {
            var binding = bindings[ordinal];
            var item = preview.Items[ordinal];
            if (binding.Ordinal != ordinal ||
                !string.Equals(binding.PeriodId, preview.CarePeriodId, StringComparison.Ordinal) ||
                !string.Equals(binding.Contributor, item.ContributorPseudonym, StringComparison.Ordinal) ||
                binding.Amount != item.AmountCents)
            {
                throw new InvalidOperationException("The payout CARE allocation binding was modified.");
            }
        }
    }

    private static string CarePeriodRegistrationSha(
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage,
        CarePeriodResult result) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            Input = input,
            Usage = usage,
            Result = result,
        })));

    private static string CarePeriodCloseReviewSha(
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage,
        CarePeriodResult result,
        DateTimeOffset reviewedUtc,
        DateTimeOffset expiresUtc) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            Input = input,
            Usage = usage,
            Result = result,
            ReviewedUtc = reviewedUtc.ToUniversalTime(),
            ExpiresUtc = expiresUtc.ToUniversalTime(),
        })));

    private static string CarePeriodCloseApprovalSha(
        string reviewSha256,
        DateTimeOffset reviewedUtc,
        DateTimeOffset expiresUtc,
        DateTimeOffset approvedUtc) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            ReviewSha256 = reviewSha256,
            ReviewedUtc = reviewedUtc.ToUniversalTime(),
            ExpiresUtc = expiresUtc.ToUniversalTime(),
            ApprovedUtc = approvedUtc.ToUniversalTime(),
        })));

    private static ContributorUsageWeight[] CanonicalizeCareUsage(
        IReadOnlyList<ContributorUsageWeight> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        if (usage.Count > MaximumCareContributorsPerPeriod || usage.Any(item =>
                item is null ||
                string.IsNullOrWhiteSpace(item.ContributorPseudonym) ||
                item.ContributorPseudonym.Length > 128 ||
                item.ContributorPseudonym.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character is not (':' or '.' or '_' or '-'))))
        {
            throw new InvalidOperationException(
                "The CARE contributor allocation set is invalid or exceeds its limit.");
        }

        return usage
            .OrderBy(item => item.ContributorPseudonym, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CarePeriodResultsEqual(
        CarePeriodResult? left,
        CarePeriodResult right) =>
        left is not null &&
        left.Year == right.Year &&
        left.Month == right.Month &&
        left.DistributableNetProfitCents == right.DistributableNetProfitCents &&
        left.AggregateContributorPoolCents == right.AggregateContributorPoolCents &&
        left.EarningPeriodClose == right.EarningPeriodClose &&
        left.Net60EligibilityDate == right.Net60EligibilityDate &&
        left.Allocations is not null &&
        left.Allocations.SequenceEqual(right.Allocations);

    private static async Task BindOrValidateStoredCarePeriodCloseReviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string periodId,
        StoredCarePeriod stored,
        DateTimeOffset reviewedUtc,
        DateTimeOffset reviewExpiresUtc,
        string closeReviewSha256,
        string closeApprovalSha256,
        DateTimeOffset approvedUtc,
        CancellationToken cancellationToken)
    {
        var closeFields = new[]
        {
            stored.CloseReviewSha256,
            stored.CloseApprovalSha256,
            stored.ReviewedUtc,
            stored.ReviewExpiresUtc,
            stored.ApprovedUtc,
        };
        if (closeFields.All(value => value is null))
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE care_periods
                SET close_review_sha256 = $review_sha,
                    close_approval_sha256 = $approval_sha,
                    reviewed_utc = $reviewed,
                    review_expires_utc = $review_expires,
                    approved_utc = $approved
                WHERE period_id = $period
                  AND close_review_sha256 IS NULL
                  AND close_approval_sha256 IS NULL
                  AND reviewed_utc IS NULL
                  AND review_expires_utc IS NULL
                  AND approved_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$review_sha", closeReviewSha256);
            command.Parameters.AddWithValue("$approval_sha", closeApprovalSha256);
            command.Parameters.AddWithValue("$reviewed", reviewedUtc.ToString("O"));
            command.Parameters.AddWithValue("$review_expires", reviewExpiresUtc.ToString("O"));
            command.Parameters.AddWithValue("$approved", approvedUtc.ToString("O"));
            command.Parameters.AddWithValue("$period", periodId);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "The CARE period close approval conflicted during registration.");
            }

            return;
        }

        if (closeFields.Any(value => value is null) ||
            !string.Equals(stored.CloseReviewSha256, closeReviewSha256, StringComparison.Ordinal) ||
            !string.Equals(stored.ReviewedUtc, reviewedUtc.ToString("O"), StringComparison.Ordinal) ||
            !string.Equals(stored.ReviewExpiresUtc, reviewExpiresUtc.ToString("O"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The CARE earning period conflicts with its approved close review.");
        }

        ValidateStoredCarePeriodCloseApproval(stored);
    }

    private static async Task<CarePeriodResult> ValidateRegisteredCarePeriodIntegrityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string periodId,
        CancellationToken cancellationToken)
    {
        var stored = await ReadCarePeriodRegistrationAsync(
                connection, transaction, periodId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The payout preview does not match a registered CARE period and eligibility date.");
        var input = new CarePeriodInput(
            stored.Year,
            stored.Month,
            stored.SubscriptionRevenueCents,
            stored.RefundChargebackCents,
            stored.PaymentFeeCents,
            stored.ApprovedOperatingCostCents,
            stored.TaxRiskReserveCents,
            stored.ContributorPoolBasisPoints,
            stored.MinimumPayoutCents);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT contributor_pseudonym, qualified_weight
            FROM care_contributor_allocations
            WHERE period_id = $period
            ORDER BY contributor_pseudonym;
            """;
        command.Parameters.AddWithValue("$period", periodId);
        var usage = new List<ContributorUsageWeight>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!decimal.TryParse(
                        reader.GetString(1),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var weight))
                {
                    throw new InvalidOperationException(
                        "The durable CARE allocation weight is invalid.");
                }

                usage.Add(new ContributorUsageWeight(reader.GetString(0), weight));
            }
        }

        CarePeriodResult result;
        try
        {
            result = CareAllocationCalculator.Calculate(input, usage);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            throw new InvalidOperationException(
                "The registered CARE period failed allocation integrity validation.");
        }

        if (!string.Equals(
                PayoutBatchPreviewFactory.CarePeriodId(result),
                periodId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The registered CARE period identifier conflicts with its economics.");
        }

        ValidateStoredCarePeriod(
            stored,
            input,
            result,
            CarePeriodRegistrationSha(input, usage, result));
        await ValidateStoredCareAllocationsAsync(
                connection, transaction, periodId, usage, result, cancellationToken)
            .ConfigureAwait(false);
        ValidateStoredCarePeriodCloseReview(stored, input, usage, result);
        return result;
    }

    private static async Task<StoredCarePeriod?> ReadCarePeriodRegistrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string periodId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT earning_year, earning_month, subscription_revenue_cents,
                   refund_chargeback_cents, payment_fee_cents,
                   approved_operating_cost_cents, tax_risk_reserve_cents,
                   contributor_pool_basis_points, minimum_payout_cents,
                   distributable_net_profit_cents, contributor_pool_cents,
                   period_close, eligibility_date, registration_sha256, registered_utc,
                   close_review_sha256, close_approval_sha256,
                   reviewed_utc, review_expires_utc, approved_utc
            FROM care_periods WHERE period_id = $period;
            """;
        command.Parameters.AddWithValue("$period", periodId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredCarePeriod(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13), reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19))
            : null;
    }

    private static void ValidateStoredCarePeriod(
        StoredCarePeriod stored,
        CarePeriodInput input,
        CarePeriodResult result,
        string registrationSha)
    {
        if (stored.Year != input.Year || stored.Month != input.Month ||
            stored.SubscriptionRevenueCents != input.SubscriptionRevenueCents ||
            stored.RefundChargebackCents != input.RefundAndChargebackCents ||
            stored.PaymentFeeCents != input.PaymentFeeCents ||
            stored.ApprovedOperatingCostCents != input.ApprovedOperatingCostCents ||
            stored.TaxRiskReserveCents != input.TaxAndRiskReserveCents ||
            stored.ContributorPoolBasisPoints != input.ContributorPoolBasisPoints ||
            stored.MinimumPayoutCents != input.MinimumPayoutCents ||
            stored.DistributableNetProfitCents != result.DistributableNetProfitCents ||
            stored.ContributorPoolCents != result.AggregateContributorPoolCents ||
            !string.Equals(stored.PeriodClose, result.EarningPeriodClose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(stored.EligibilityDate, result.Net60EligibilityDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(stored.RegistrationSha256, registrationSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The CARE earning period conflicts with prior registration.");
        }
    }

    private static async Task ValidateStoredCareAllocationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string periodId,
        IReadOnlyList<ContributorUsageWeight> usage,
        CarePeriodResult result,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT contributor_pseudonym, qualified_weight,
                   allocation_cents, held_below_threshold
            FROM care_contributor_allocations
            WHERE period_id = $period
            ORDER BY contributor_pseudonym;
            """;
        command.Parameters.AddWithValue("$period", periodId);
        var rows = new List<(string Contributor, string Weight, long Amount, bool Held)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3) == 1));
            }
        }

        if (rows.Count != usage.Count)
        {
            throw new InvalidOperationException("The durable CARE allocation set is incomplete.");
        }

        var allocations = result.Allocations.ToDictionary(
            item => item.ContributorPseudonym,
            StringComparer.Ordinal);
        for (var index = 0; index < usage.Count; index++)
        {
            var expectedUsage = usage[index];
            var row = rows[index];
            allocations.TryGetValue(expectedUsage.ContributorPseudonym, out var allocation);
            if (!string.Equals(row.Contributor, expectedUsage.ContributorPseudonym, StringComparison.Ordinal) ||
                !string.Equals(row.Weight, expectedUsage.Weight.ToString("G29", CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                row.Amount != (allocation?.AmountCents ?? 0) ||
                row.Held != (allocation?.HeldBelowThreshold is not false))
            {
                throw new InvalidOperationException("The durable CARE allocation set conflicts with registration.");
            }
        }
    }

    private static void ValidateStoredCarePeriodCloseReview(
        StoredCarePeriod stored,
        CarePeriodInput input,
        IReadOnlyList<ContributorUsageWeight> usage,
        CarePeriodResult result)
    {
        if (!PayoutContractValidation.IsLowerSha256(stored.CloseReviewSha256) ||
            !DateTimeOffset.TryParse(
                stored.ReviewedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var reviewedUtc) ||
            !DateTimeOffset.TryParse(
                stored.ReviewExpiresUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresUtc) ||
            !DateTimeOffset.TryParse(
                stored.ApprovedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var approvedUtc) ||
            reviewedUtc.Offset != TimeSpan.Zero ||
            expiresUtc.Offset != TimeSpan.Zero ||
            approvedUtc.Offset != TimeSpan.Zero ||
            expiresUtc - reviewedUtc != CarePeriodCloseReviewLifetime ||
            approvedUtc < reviewedUtc || approvedUtc > expiresUtc)
        {
            throw new InvalidOperationException(
                "The registered CARE period lacks a valid approved close review.");
        }

        ValidateStoredCarePeriodCloseApproval(stored);

        var expected = CarePeriodCloseReviewSha(
            input, usage, result, reviewedUtc, expiresUtc);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(stored.CloseReviewSha256!)))
        {
            throw new InvalidOperationException(
                "The registered CARE period close review failed integrity validation.");
        }
    }

    private static void ValidateStoredCarePeriodCloseApproval(StoredCarePeriod stored)
    {
        if (!PayoutContractValidation.IsLowerSha256(stored.CloseApprovalSha256) ||
            !DateTimeOffset.TryParse(
                stored.ReviewedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var reviewedUtc) ||
            !DateTimeOffset.TryParse(
                stored.ReviewExpiresUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var expiresUtc) ||
            !DateTimeOffset.TryParse(
                stored.ApprovedUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var approvedUtc))
        {
            throw new InvalidOperationException(
                "The registered CARE period lacks a valid close approval binding.");
        }

        var expected = CarePeriodCloseApprovalSha(
            stored.CloseReviewSha256!, reviewedUtc, expiresUtc, approvedUtc);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(stored.CloseApprovalSha256!)))
        {
            throw new InvalidOperationException(
                "The registered CARE period close approval failed integrity validation.");
        }
    }

    private sealed record StoredCarePeriod(
        int Year,
        int Month,
        long SubscriptionRevenueCents,
        long RefundChargebackCents,
        long PaymentFeeCents,
        long ApprovedOperatingCostCents,
        long TaxRiskReserveCents,
        int ContributorPoolBasisPoints,
        long MinimumPayoutCents,
        long DistributableNetProfitCents,
        long ContributorPoolCents,
        string PeriodClose,
        string EligibilityDate,
        string RegistrationSha256,
        string RegisteredUtc,
        string? CloseReviewSha256,
        string? CloseApprovalSha256,
        string? ReviewedUtc,
        string? ReviewExpiresUtc,
        string? ApprovedUtc);
}
