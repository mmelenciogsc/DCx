using DCx.Core.Release;

namespace DCx.Core.Tests.Release;

public sealed class PilotMetricAggregateStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-pilot-{Guid.NewGuid():N}");
    private static readonly DateOnly Day = new(2026, 10, 17);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task Recording_DefaultsOffAndCreatesNoRawEvent()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(root);

        Assert.Equal(new PilotMetricConsentStatus(false, 0, null), await store.GetConsentAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordAsync("task_duration_ms", Day, 100));
        Assert.Empty(await store.QueryAsync(Day, Day));
        Assert.Equal(0, await store.GetAggregateRowCountAsync());
    }

    [Fact]
    public async Task OptIn_AggregatesByDayAndEnumDimensionAcrossRestart()
    {
        await using (var store = await PilotMetricAggregateStore.OpenAsync(root))
        {
            await store.SetConsentAsync(true, 1, Now);
            await store.RecordAsync("task_duration_ms", Day, 100);
            await store.RecordAsync("task_duration_ms", Day, 250);
            await store.RecordAsync(
                "model_input_tokens",
                Day,
                400,
                PilotMetricDimension.DeterministicRoute);
        }

        await using var reopened = await PilotMetricAggregateStore.OpenAsync(root);
        Assert.Equal(new PilotMetricConsentStatus(true, 1, Now), await reopened.GetConsentAsync());
        var aggregates = await reopened.QueryAsync(Day, Day);
        Assert.Equal(2, aggregates.Count);
        Assert.Equal(2, await reopened.GetAggregateRowCountAsync());
        Assert.Contains(aggregates, aggregate =>
            aggregate == new PilotMetricAggregate(
                "task_duration_ms",
                Day,
                PilotMetricDimension.Aggregate,
                2,
                350,
                100,
                250));
        Assert.Contains(aggregates, aggregate =>
            aggregate.MetricKey == "model_input_tokens" &&
            aggregate.Dimension == PilotMetricDimension.DeterministicRoute &&
            aggregate.ValueSum == 400);
    }

    [Fact]
    public async Task OptOut_PurgesAggregatesAndStopsRecording()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(root);
        await store.SetConsentAsync(true, 3, Now);
        await store.RecordAsync("crash_count", Day, 1, PilotMetricDimension.WebViewError);

        await store.SetConsentAsync(false, 3, Now.AddMinutes(5));

        Assert.False((await store.GetConsentAsync()).Enabled);
        Assert.Empty(await store.QueryAsync(Day, Day));
        Assert.Equal(0, await store.GetAggregateRowCountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RecordAsync("crash_count", Day, 1, PilotMetricDimension.WebViewError));
    }

    [Fact]
    public async Task Recording_RejectsUncatalogedMetricFreeFormDimensionAndInvalidRatio()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(root);
        await store.SetConsentAsync(true, 1, Now);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordAsync("raw_prompt", Day, 1));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordAsync("task_duration_ms", Day, 10, PilotMetricDimension.StartupError));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.RecordAsync("task_completion_rate", Day, 50));
    }

    [Fact]
    public async Task ConsentNoticeVersion_CannotMoveBackward()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(root);
        await store.SetConsentAsync(true, 4, Now);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetConsentAsync(true, 3, Now.AddMinutes(1)));

        Assert.Equal(4, (await store.GetConsentAsync()).NoticeVersion);
    }

    [Fact]
    public async Task OptOut_WithOlderNoticeVersion_PreservesVersionAndPurges()
    {
        await using var store = await PilotMetricAggregateStore.OpenAsync(root);
        await store.SetConsentAsync(true, 4, Now);
        await store.RecordAsync("task_duration_ms", Day, 100);

        await store.SetConsentAsync(false, 1, Now.AddMinutes(1));

        Assert.Equal(new PilotMetricConsentStatus(false, 4, Now.AddMinutes(1)), await store.GetConsentAsync());
        Assert.Equal(0, await store.GetAggregateRowCountAsync());
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }
}
