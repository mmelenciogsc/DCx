using System.Text.Json;
using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class LocalCrashReportStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-crash-reports-{Guid.NewGuid():N}");
    private readonly DateTimeOffset now = DateTimeOffset.Parse("2026-10-17T00:00:00Z");

    [Fact]
    public async Task DefaultOff_DoesNotPersistExceptionContent()
    {
        var store = await LocalCrashReportStore.OpenAsync(root);
        var exception = new InvalidOperationException(
            "secret " + "sk-" + "abcdefghijklmnopqrstuvwxyz123456" +
            " at C:\\Users\\Person\\private.txt https://example.test/account");

        var recorded = await store.RecordExceptionAsync(
            exception,
            CrashComponent.UserInterface,
            processTerminating: false,
            "1.2.3",
            now);
        var status = await store.GetStatusAsync();

        Assert.False(recorded);
        Assert.False(status.Consent.Enabled);
        Assert.Equal(0, status.RetainedReportCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "reliability", "crash-reports")));
    }

    [Fact]
    public async Task OptIn_StoresOnlyClosedMinimizedFieldsAndBoundsRetention()
    {
        var store = await LocalCrashReportStore.OpenAsync(root);
        await store.SetConsentAsync(true, LocalCrashReportStore.CurrentNoticeVersion, now);
        var privateText = "sk-" + "abcdefghijklmnopqrstuvwxyz123456" +
                          " C:\\Users\\Person\\private.txt https://example.test/account";

        Assert.True(await store.RecordExceptionAsync(
            new IOException(privateText),
            CrashComponent.ProcessTermination,
            processTerminating: true,
            "1.2.3-test",
            now));
        for (var index = 1; index <= LocalCrashReportStore.MaximumRetainedReports + 5; index++)
        {
            Assert.True(await store.RecordAsync(
                CrashComponent.BackgroundTask,
                CrashErrorCategory.Unknown,
                processTerminating: false,
                "1.2.3-test",
                now.AddSeconds(index)));
        }

        var reportsRoot = Path.Combine(root, "reliability", "crash-reports");
        var reports = Directory.GetFiles(reportsRoot).Order(StringComparer.Ordinal).ToArray();
        var status = await store.GetStatusAsync();
        var content = await File.ReadAllTextAsync(reports[^1]);
        using var document = JsonDocument.Parse(content);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        Assert.True(status.Consent.Enabled);
        Assert.Equal(LocalCrashReportStore.MaximumRetainedReports, reports.Length);
        Assert.Equal(LocalCrashReportStore.MaximumRetainedReports, status.RetainedReportCount);
        Assert.Equal(
            ["schemaVersion", "recordedUtc", "component", "errorCategory", "processTerminating", "applicationVersion"],
            properties);
        Assert.DoesNotContain(privateText, content, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("message", content, StringComparison.OrdinalIgnoreCase);

        await store.PurgeReportsAsync();
        var purged = await store.GetStatusAsync();
        Assert.True(purged.Consent.Enabled);
        Assert.Equal(0, purged.RetainedReportCount);
    }

    [Fact]
    public async Task OptOut_PurgesReportsAndNoticeVersionCannotRollback()
    {
        var store = await LocalCrashReportStore.OpenAsync(root);
        await store.SetConsentAsync(true, 2, now);
        await store.RecordAsync(
            CrashComponent.ApplicationStartup,
            CrashErrorCategory.ExternalComponent,
            processTerminating: false,
            "2.0.0",
            now);

        await store.SetConsentAsync(false, 2, now.AddMinutes(1));
        var status = await store.GetStatusAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SetConsentAsync(true, 1, now.AddMinutes(2)));
        var reopened = await LocalCrashReportStore.OpenAsync(root);

        Assert.False(status.Consent.Enabled);
        Assert.Equal(0, status.RetainedReportCount);
        Assert.Equal(0, (await reopened.GetStatusAsync()).RetainedReportCount);
    }

    [Fact]
    public async Task Admission_RejectsFilesystemRootLinksAndMalformedConsent()
    {
        var outside = Path.Combine(root, "outside");
        var linked = Path.Combine(root, "linked");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linked, outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalCrashReportStore.OpenAsync(linked));
        Assert.Throws<InvalidOperationException>(() =>
            LocalCrashReportStore.OpenAsync(Path.GetPathRoot(root)!).GetAwaiter().GetResult());

        var validRoot = Path.Combine(root, "valid");
        var store = await LocalCrashReportStore.OpenAsync(validRoot);
        await store.SetConsentAsync(true, 1, now);
        await File.WriteAllTextAsync(
            Path.Combine(validRoot, "reliability", "crash-report-consent.json"),
            "{\"schemaVersion\":1,\"enabled\":true,\"noticeVersion\":1,\"recordedUtc\":\"2026-10-17T00:00:00.0000000+00:00\",\"unexpected\":true}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalCrashReportStore.OpenAsync(validRoot));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);
}
