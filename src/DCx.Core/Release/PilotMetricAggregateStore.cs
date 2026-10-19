using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Release;

public enum PilotMetricDimension
{
    Aggregate = 0,
    DeterministicRoute = 1,
    LocalRoute = 2,
    OpenAiStandardRoute = 3,
    OpenAiHighRoute = 4,
    StartupError = 5,
    WebViewError = 6,
    BrokerError = 7,
    ModelError = 8,
    SyncError = 9,
    OtherError = 10,
    ClaimMismatch = 11,
    ClaimUnverified = 12,
    KeyboardAccess = 13,
    ScreenReaderName = 14,
    FocusOrder = 15,
    LiveRegion = 16,
}

public sealed record PilotMetricConsentStatus(bool Enabled, int NoticeVersion, DateTimeOffset? RecordedUtc);

public sealed record PilotMetricAggregate(
    string MetricKey,
    DateOnly DayUtc,
    PilotMetricDimension Dimension,
    long SampleCount,
    long ValueSum,
    long Minimum,
    long Maximum);

public sealed class PilotMetricAggregateStore : IAsyncDisposable
{
    private const long MaximumAbsoluteValue = 1_000_000_000_000;
    private const long MaximumAggregateSamples = 1_000_000_000;
    private const int MaximumQueryRows = 10_000;

    private static readonly HashSet<PilotMetricDimension> RouteDimensions =
    [
        PilotMetricDimension.DeterministicRoute,
        PilotMetricDimension.LocalRoute,
        PilotMetricDimension.OpenAiStandardRoute,
        PilotMetricDimension.OpenAiHighRoute,
    ];

    private static readonly HashSet<PilotMetricDimension> ErrorDimensions =
    [
        PilotMetricDimension.StartupError,
        PilotMetricDimension.WebViewError,
        PilotMetricDimension.BrokerError,
        PilotMetricDimension.ModelError,
        PilotMetricDimension.SyncError,
        PilotMetricDimension.OtherError,
    ];

    private static readonly HashSet<PilotMetricDimension> ClaimDimensions =
    [
        PilotMetricDimension.ClaimMismatch,
        PilotMetricDimension.ClaimUnverified,
    ];

    private static readonly HashSet<PilotMetricDimension> AccessibilityDimensions =
    [
        PilotMetricDimension.KeyboardAccess,
        PilotMetricDimension.ScreenReaderName,
        PilotMetricDimension.FocusOrder,
        PilotMetricDimension.LiveRegion,
    ];

    private readonly string connectionString;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    private PilotMetricAggregateStore(string databasePath)
    {
        DatabasePath = databasePath;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public static async Task<PilotMetricAggregateStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        var applicationRoot = Path.GetFullPath(applicationDataRoot);
        var pathRoot = Path.GetPathRoot(applicationRoot);
        if (pathRoot is not null && string.Equals(
                applicationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The pilot metric store cannot use a filesystem root.");
        }

        Directory.CreateDirectory(applicationRoot);
        if ((File.GetAttributes(applicationRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The pilot metric application-data root cannot be a reparse point.");
        }

        var root = Path.Combine(applicationRoot, "pilot");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The pilot metric store cannot use a reparse-point directory.");
        }

        var databasePath = Path.Combine(root, "aggregate-metrics.sqlite3");
        if (File.Exists(databasePath) && (File.GetAttributes(databasePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The pilot metric database cannot be a reparse point.");
        }

        var store = new PilotMetricAggregateStore(databasePath);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<PilotMetricConsentStatus> GetConsentAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT enabled, notice_version, recorded_utc FROM consent WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PilotMetricConsentStatus(
                reader.GetInt64(0) == 1,
                reader.GetInt32(1),
                DateTimeOffset.ParseExact(
                    reader.GetString(2),
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind))
            : new PilotMetricConsentStatus(false, 0, null);
    }

    public async Task SetConsentAsync(
        bool enabled,
        int noticeVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (noticeVersion is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(noticeVersion));
        }

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT notice_version FROM consent WHERE singleton = 1;";
            var existingNoticeVersion = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var effectiveNoticeVersion = noticeVersion;
            if (existingNoticeVersion is long currentVersion && noticeVersion < currentVersion)
            {
                if (enabled)
                {
                    throw new InvalidOperationException("The pilot consent notice version cannot move backward.");
                }

                effectiveNoticeVersion = checked((int)currentVersion);
            }

            if (!enabled)
            {
                command.CommandText = "DELETE FROM metric_aggregates;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO consent(singleton, enabled, notice_version, recorded_utc)
                VALUES(1, $enabled, $version, $recorded)
                ON CONFLICT(singleton) DO UPDATE SET
                    enabled = excluded.enabled,
                    notice_version = excluded.notice_version,
                    recorded_utc = excluded.recorded_utc;
                """;
            command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("$version", effectiveNoticeVersion);
            command.Parameters.AddWithValue("$recorded", now.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task RecordAsync(
        string metricKey,
        DateOnly dayUtc,
        long value,
        PilotMetricDimension dimension = PilotMetricDimension.Aggregate,
        CancellationToken cancellationToken = default)
    {
        ValidateMeasurement(metricKey, dayUtc, value, dimension);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT enabled FROM consent WHERE singleton = 1;";
            var consent = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (consent is not long enabled || enabled != 1)
            {
                throw new InvalidOperationException("Pilot metric recording is disabled until the user opts in.");
            }

            var day = dayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            command.Parameters.Clear();
            command.CommandText = """
                SELECT sample_count, value_sum, minimum, maximum
                FROM metric_aggregates
                WHERE metric_key = $metric AND day_utc = $day AND dimension = $dimension;
                """;
            command.Parameters.AddWithValue("$metric", metricKey);
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$dimension", dimension.ToString());
            long sampleCount = 0;
            long valueSum = 0;
            long minimum = value;
            long maximum = value;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    sampleCount = reader.GetInt64(0);
                    valueSum = reader.GetInt64(1);
                    minimum = Math.Min(reader.GetInt64(2), value);
                    maximum = Math.Max(reader.GetInt64(3), value);
                }
            }

            sampleCount = checked(sampleCount + 1);
            valueSum = checked(valueSum + value);
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO metric_aggregates(
                    metric_key, day_utc, dimension, sample_count, value_sum, minimum, maximum)
                VALUES($metric, $day, $dimension, $count, $sum, $minimum, $maximum)
                ON CONFLICT(metric_key, day_utc, dimension) DO UPDATE SET
                    sample_count = excluded.sample_count,
                    value_sum = excluded.value_sum,
                    minimum = excluded.minimum,
                    maximum = excluded.maximum;
                """;
            command.Parameters.AddWithValue("$metric", metricKey);
            command.Parameters.AddWithValue("$day", day);
            command.Parameters.AddWithValue("$dimension", dimension.ToString());
            command.Parameters.AddWithValue("$count", sampleCount);
            command.Parameters.AddWithValue("$sum", valueSum);
            command.Parameters.AddWithValue("$minimum", minimum);
            command.Parameters.AddWithValue("$maximum", maximum);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<PilotMetricAggregate>> QueryAsync(
        DateOnly firstDayUtc,
        DateOnly lastDayUtc,
        CancellationToken cancellationToken = default)
    {
        if (lastDayUtc < firstDayUtc || lastDayUtc.DayNumber - firstDayUtc.DayNumber >= 366)
        {
            throw new ArgumentOutOfRangeException(nameof(lastDayUtc));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metric_key, day_utc, dimension, sample_count, value_sum, minimum, maximum
            FROM metric_aggregates
            WHERE day_utc >= $first AND day_utc <= $last
            ORDER BY day_utc, metric_key, dimension
            LIMIT 10001;
            """;
        command.Parameters.AddWithValue("$first", firstDayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last", lastDayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var results = new List<PilotMetricAggregate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (results.Count == MaximumQueryRows)
            {
                throw new InvalidOperationException("The requested pilot metric range exceeds the bounded result set.");
            }

            results.Add(ReadStoredAggregate(reader));
        }

        return results;
    }

    public async Task<int> GetAggregateRowCountAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM metric_aggregates;";
        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (count is not long value || value is < 0 or > int.MaxValue)
        {
            throw new InvalidOperationException("The stored pilot metric aggregate count is invalid.");
        }

        return checked((int)value);
    }

    public ValueTask DisposeAsync()
    {
        writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS consent(
                singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                enabled INTEGER NOT NULL CHECK(enabled IN (0, 1)),
                notice_version INTEGER NOT NULL CHECK(notice_version > 0),
                recorded_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS metric_aggregates(
                metric_key TEXT NOT NULL,
                day_utc TEXT NOT NULL,
                dimension TEXT NOT NULL,
                sample_count INTEGER NOT NULL CHECK(sample_count > 0),
                value_sum INTEGER NOT NULL,
                minimum INTEGER NOT NULL,
                maximum INTEGER NOT NULL,
                PRIMARY KEY(metric_key, day_utc, dimension)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static PilotMetricAggregate ReadStoredAggregate(SqliteDataReader reader)
    {
        string metricKey;
        string storedDay;
        string storedDimension;
        long sampleCount;
        long valueSum;
        long minimum;
        long maximum;
        try
        {
            metricKey = reader.GetString(0);
            storedDay = reader.GetString(1);
            storedDimension = reader.GetString(2);
            sampleCount = reader.GetInt64(3);
            valueSum = reader.GetInt64(4);
            minimum = reader.GetInt64(5);
            maximum = reader.GetInt64(6);
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException("A stored pilot metric aggregate has invalid field types.", exception);
        }

        if (!DateOnly.TryParseExact(
                storedDay,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dayUtc) ||
            !Enum.TryParse<PilotMetricDimension>(storedDimension, ignoreCase: false, out var dimension) ||
            !Enum.IsDefined(dimension))
        {
            throw new InvalidOperationException("A stored pilot metric day or dimension is invalid.");
        }

        if (sampleCount is < 1 or > MaximumAggregateSamples || minimum > maximum)
        {
            throw new InvalidOperationException("A stored pilot metric aggregate has invalid numeric bounds.");
        }

        try
        {
            ValidateMeasurement(metricKey, dayUtc, minimum, dimension);
            ValidateMeasurement(metricKey, dayUtc, maximum, dimension);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("A stored pilot metric aggregate violates the reviewed catalog.", exception);
        }

        var lowestPossibleSum = (decimal)minimum * sampleCount;
        var highestPossibleSum = (decimal)maximum * sampleCount;
        if (valueSum < lowestPossibleSum || valueSum > highestPossibleSum)
        {
            throw new InvalidOperationException("A stored pilot metric aggregate has an impossible sum.");
        }

        return new PilotMetricAggregate(
            metricKey,
            dayUtc,
            dimension,
            sampleCount,
            valueSum,
            minimum,
            maximum);
    }

    private static void ValidateMeasurement(
        string metricKey,
        DateOnly dayUtc,
        long value,
        PilotMetricDimension dimension)
    {
        if (!PilotMetricCatalog.All.Any(metric => string.Equals(metric.Key, metricKey, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The pilot metric is not in the privacy-reviewed catalog.", nameof(metricKey));
        }

        if (dayUtc.Year is < 2000 or > 2100)
        {
            throw new ArgumentOutOfRangeException(nameof(dayUtc));
        }

        if (Math.Abs((decimal)value) > MaximumAbsoluteValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var permittedDimensions = metricKey switch
        {
            "model_input_tokens" or "model_output_tokens" => RouteDimensions,
            "crash_count" => ErrorDimensions,
            "false_agent_claim_count" => ClaimDimensions,
            "accessibility_failure_count" => AccessibilityDimensions,
            _ => null,
        };
        if ((permittedDimensions is null && dimension != PilotMetricDimension.Aggregate) ||
            (permittedDimensions is not null && !permittedDimensions.Contains(dimension)))
        {
            throw new ArgumentException("The pilot metric dimension is not approved for this metric.", nameof(dimension));
        }

        if ((metricKey.EndsWith("_rate", StringComparison.Ordinal) ||
             metricKey.EndsWith("_ratio", StringComparison.Ordinal)) && value is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Ratio samples must be zero or one.");
        }

        if (metricKey != "care_reconciliation_variance_cents" && value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "This metric cannot be negative.");
        }
    }
}
