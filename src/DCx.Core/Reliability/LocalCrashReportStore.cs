using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCx.Core.Reliability;

public enum CrashComponent
{
    ApplicationStartup = 1,
    UserInterface = 2,
    BackgroundTask = 3,
    ProcessTermination = 4,
}

public enum CrashErrorCategory
{
    Argument = 1,
    InputOutput = 2,
    AccessDenied = 3,
    InvalidOperation = 4,
    ResourceExhaustion = 5,
    Cancelled = 6,
    ExternalComponent = 7,
    Unknown = 8,
}

public sealed record CrashReportConsentStatus(
    bool Enabled,
    int NoticeVersion,
    DateTimeOffset? RecordedUtc);

public sealed record LocalCrashReport(
    int SchemaVersion,
    DateTimeOffset RecordedUtc,
    CrashComponent Component,
    CrashErrorCategory ErrorCategory,
    bool ProcessTerminating,
    string ApplicationVersion);

public sealed record LocalCrashReportStatus(
    CrashReportConsentStatus Consent,
    int RetainedReportCount);

public sealed class LocalCrashReportStore
{
    public const int CurrentNoticeVersion = 1;
    public const int MaximumRetainedReports = 20;
    public const int MaximumReportBytes = 16 * 1024;

    private const int MaximumConsentBytes = 16 * 1024;
    private const string ConsentFileName = "crash-report-consent.json";
    private const string ReportsDirectoryName = "crash-reports";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 8,
    };

    private readonly string consentPath;
    private readonly string reportsRoot;
    private readonly SemaphoreSlim mutationLock = new(1, 1);

    private LocalCrashReportStore(string reliabilityRoot)
    {
        consentPath = Path.Combine(reliabilityRoot, ConsentFileName);
        reportsRoot = Path.Combine(reliabilityRoot, ReportsDirectoryName);
    }

    public static async Task<LocalCrashReportStore> OpenAsync(
        string applicationDataRoot,
        CancellationToken cancellationToken = default)
    {
        var applicationRoot = ValidateRoot(applicationDataRoot, "application-data");
        var reliabilityRoot = Path.Combine(applicationRoot, "reliability");
        Directory.CreateDirectory(reliabilityRoot);
        RejectReparsePoint(reliabilityRoot, "The reliability directory cannot be a reparse point.");
        var store = new LocalCrashReportStore(reliabilityRoot);
        Directory.CreateDirectory(store.reportsRoot);
        RejectReparsePoint(store.reportsRoot, "The crash-report directory cannot be a reparse point.");
        if (File.Exists(store.consentPath))
        {
            RejectReparsePoint(store.consentPath, "The crash-report consent file cannot be a reparse point.");
        }

        var consent = await store.ReadConsentUnsafeAsync(cancellationToken).ConfigureAwait(false);
        if (!consent.Enabled)
        {
            await store.PurgeReportsUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await store.PruneReportsUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }

        return store;
    }

    public async Task<LocalCrashReportStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var consent = await ReadConsentUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var count = EnumerateReportFiles().Count;
            return new LocalCrashReportStatus(consent, count);
        }
        finally
        {
            mutationLock.Release();
        }
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

        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadConsentUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (noticeVersion < current.NoticeVersion)
            {
                throw new InvalidOperationException("The crash-report consent notice version cannot move backward.");
            }

            var next = new CrashReportConsentFile(
                1,
                enabled,
                noticeVersion,
                now.ToUniversalTime());
            await WriteAtomicJsonAsync(consentPath, next, MaximumConsentBytes, cancellationToken)
                .ConfigureAwait(false);
            if (!enabled)
            {
                await PurgeReportsUnsafeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task PurgeReportsAsync(CancellationToken cancellationToken = default)
    {
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PurgeReportsUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public async Task<bool> RecordAsync(
        CrashComponent component,
        CrashErrorCategory errorCategory,
        bool processTerminating,
        string applicationVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateReportFields(component, errorCategory, applicationVersion);
        await mutationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var consent = await ReadConsentUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!consent.Enabled) return false;

            var report = new LocalCrashReport(
                1,
                now.ToUniversalTime(),
                component,
                errorCategory,
                processTerminating,
                applicationVersion);
            var name = $"crash-{now.ToUniversalTime().Ticks:D19}-{Guid.NewGuid():N}.json";
            await WriteAtomicJsonAsync(
                    Path.Combine(reportsRoot, name),
                    report,
                    MaximumReportBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            await PruneReportsUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutationLock.Release();
        }
    }

    public Task<bool> RecordExceptionAsync(
        Exception exception,
        CrashComponent component,
        bool processTerminating,
        string applicationVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return RecordAsync(
            component,
            Classify(exception),
            processTerminating,
            applicationVersion,
            now,
            cancellationToken);
    }

    public static CrashErrorCategory Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            OperationCanceledException => CrashErrorCategory.Cancelled,
            UnauthorizedAccessException => CrashErrorCategory.AccessDenied,
            OutOfMemoryException => CrashErrorCategory.ResourceExhaustion,
            IOException => CrashErrorCategory.InputOutput,
            ArgumentException => CrashErrorCategory.Argument,
            InvalidOperationException => CrashErrorCategory.InvalidOperation,
            ExternalException or Win32Exception => CrashErrorCategory.ExternalComponent,
            _ => CrashErrorCategory.Unknown,
        };
    }

    private async Task<CrashReportConsentStatus> ReadConsentUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(consentPath)) return new CrashReportConsentStatus(false, 0, null);
        RejectReparsePoint(consentPath, "The crash-report consent file cannot be a reparse point.");
        var file = new FileInfo(consentPath);
        if (file.Length is <= 0 or > MaximumConsentBytes)
        {
            throw new InvalidOperationException("The crash-report consent file is invalid.");
        }

        try
        {
            await using var stream = new FileStream(
                consentPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var consent = await JsonSerializer.DeserializeAsync<CrashReportConsentFile>(
                    stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (consent is null || consent.SchemaVersion != 1 ||
                consent.NoticeVersion is < 1 or > 10_000 ||
                consent.RecordedUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidOperationException("The crash-report consent file is invalid.");
            }

            return new CrashReportConsentStatus(
                consent.Enabled,
                consent.NoticeVersion,
                consent.RecordedUtc);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The crash-report consent file is malformed.");
        }
    }

    private async Task PruneReportsUnsafeAsync(CancellationToken cancellationToken)
    {
        var files = EnumerateReportFiles();
        foreach (var file in files.Take(Math.Max(0, files.Count - MaximumRetainedReports)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file.FullName);
        }

        await Task.CompletedTask;
    }

    private async Task PurgeReportsUnsafeAsync(CancellationToken cancellationToken)
    {
        foreach (var file in EnumerateReportFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file.FullName);
        }

        await Task.CompletedTask;
    }

    private List<FileInfo> EnumerateReportFiles()
    {
        RejectReparsePoint(reportsRoot, "The crash-report directory cannot be a reparse point.");
        var files = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFileSystemEntries(reportsRoot))
        {
            RejectReparsePoint(path, "A crash-report entry cannot be a reparse point.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                !Path.GetFileName(path).StartsWith("crash-", StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.Ordinal) ||
                new FileInfo(path).Length is <= 0 or > MaximumReportBytes)
            {
                throw new InvalidOperationException("The crash-report directory contains an invalid entry.");
            }

            files.Add(new FileInfo(path));
        }

        return files.OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
    }

    private static async Task WriteAtomicJsonAsync<T>(
        string destination,
        T value,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (content.Length is <= 0 || content.Length > maximumBytes)
        {
            throw new InvalidOperationException("The local crash-report record exceeds its size limit.");
        }

        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The local crash-report destination has no parent directory.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ValidateRoot(string root, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var canonical = Path.GetFullPath(root);
        var pathRoot = Path.GetPathRoot(canonical);
        if (pathRoot is not null && canonical.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The crash-report {label} root cannot be a filesystem root.");
        }

        Directory.CreateDirectory(canonical);
        RejectReparsePoint(canonical, $"The crash-report {label} root cannot be a reparse point.");
        return canonical;
    }

    private static void RejectReparsePoint(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void ValidateReportFields(
        CrashComponent component,
        CrashErrorCategory category,
        string applicationVersion)
    {
        if (!Enum.IsDefined(component) || !Enum.IsDefined(category) ||
            string.IsNullOrWhiteSpace(applicationVersion) || applicationVersion.Length > 64 ||
            applicationVersion.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '+' and not '_'))
        {
            throw new ArgumentException("The local crash-report fields are invalid.");
        }
    }

    private sealed record CrashReportConsentFile(
        int SchemaVersion,
        bool Enabled,
        int NoticeVersion,
        DateTimeOffset RecordedUtc);
}
