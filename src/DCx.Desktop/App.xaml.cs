using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using DCx.Core.Deployment;
using DCx.Core.LocalModels;
using DCx.Core.Reliability;
using DCx.Core.Storage;

namespace DCx.Desktop;

public partial class App : Application
{
    private ApplicationDataOperationLease? runtimeLease;
    private LocalCrashReportStore? crashReportStore;
    private bool crashHandlersRegistered;

    internal LocalCrashReportStore? CrashReportStore => crashReportStore;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 &&
            string.Equals(e.Args[0], "--package-approval-fixture", StringComparison.Ordinal))
        {
            StartPackageApprovalFixture(e.Args[1]);
            return;
        }

        if (e.Args.Length == 2 &&
            string.Equals(e.Args[0], "--whisper-staging-fixture", StringComparison.Ordinal))
        {
            StartWhisperStagingFixture(e.Args[1]);
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--recovery", StringComparison.Ordinal))
        {
            MainWindow = new RecoveryWindow();
            MainWindow.Show();
            return;
        }

        if (e.Args.Length == 1 && string.Equals(e.Args[0], "--readiness", StringComparison.Ordinal))
        {
            MainWindow = new ReadinessWindow();
            MainWindow.Show();
            return;
        }

        IWebView2RuntimeProbe? webView2RuntimeProbe = null;
        if (e.Args.Length == 1 &&
            string.Equals(e.Args[0], "--webview2-unavailable-fixture", StringComparison.Ordinal))
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("DCX_WEBVIEW2_UNAVAILABLE_FIXTURE"),
                    "1",
                    StringComparison.Ordinal))
            {
                Shutdown(2);
                return;
            }

            webView2RuntimeProbe = new MissingWebView2RuntimeFixtureProbe();
        }

        try
        {
            var applicationDataRoot = ApplicationDataRoot.Resolve();
            runtimeLease = ApplicationDataOperationLease.Acquire(applicationDataRoot, "starting DCx");
            TryInitializeCrashDiagnostics(applicationDataRoot);
            MainWindow = webView2RuntimeProbe is null
                ? new MainWindow()
                : new MainWindow(webView2RuntimeProbe);
            MainWindow.Show();
        }
        catch (ApplicationDataInUseException exception)
        {
            MessageBox.Show(
                exception.Message,
                "DCx data is in use",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterCrashHandlers();
        runtimeLease?.Dispose();
        base.OnExit(e);
    }

    private void TryInitializeCrashDiagnostics(string applicationDataRoot)
    {
        try
        {
            crashReportStore = LocalCrashReportStore.OpenAsync(applicationDataRoot).GetAwaiter().GetResult();
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            crashHandlersRegistered = true;
        }
        catch
        {
            // Optional local diagnostics must never prevent the browser from starting.
            crashReportStore = null;
        }
    }

    private void UnregisterCrashHandlers()
    {
        if (!crashHandlersRegistered) return;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        crashHandlersRegistered = false;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        TryRecordException(e.Exception, CrashComponent.UserInterface, processTerminating: false);

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            TryRecordException(exception, CrashComponent.ProcessTermination, e.IsTerminating);
        }
        else
        {
            TryRecordCategory(CrashComponent.ProcessTermination, CrashErrorCategory.Unknown, e.IsTerminating);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        TryRecordException(e.Exception, CrashComponent.BackgroundTask, processTerminating: false);

    private void TryRecordException(Exception exception, CrashComponent component, bool processTerminating)
    {
        try
        {
            crashReportStore?.RecordExceptionAsync(
                    exception,
                    component,
                    processTerminating,
                    GetApplicationVersion(),
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Crash recording is best effort and must not alter exception handling.
        }
    }

    private void TryRecordCategory(
        CrashComponent component,
        CrashErrorCategory category,
        bool processTerminating)
    {
        try
        {
            crashReportStore?.RecordAsync(
                    component,
                    category,
                    processTerminating,
                    GetApplicationVersion(),
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // Crash recording is best effort and must not alter exception handling.
        }
    }

    private static string GetApplicationVersion() =>
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private void StartPackageApprovalFixture(string packagePath)
    {
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("DCX_PACKAGE_APPROVAL_FIXTURE"),
                    "1",
                    StringComparison.Ordinal) ||
                !IsWithinFixtureRoot(packagePath, "DCX_PACKAGE_APPROVAL_FIXTURE_ROOT"))
            {
                throw new PackageInstallReviewException("package_review_fixture_disabled");
            }

            var session = PackageInstallReviewService.BeginAsync(
                    packagePath,
                    new SyntheticPackageTrustVerifier(),
                    DateTimeOffset.UtcNow)
                .GetAwaiter()
                .GetResult();
            MainWindow = new PackageInstallReviewWindow(session);
            MainWindow.Show();
        }
        catch (PackageInstallReviewException)
        {
            Shutdown(2);
        }
    }

    private void StartWhisperStagingFixture(string archivePath)
    {
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("DCX_WHISPER_STAGING_FIXTURE"),
                    "1",
                    StringComparison.Ordinal) ||
                !IsWithinFixtureRoot(archivePath, "DCX_WHISPER_STAGING_FIXTURE_ROOT"))
            {
                throw new InvalidOperationException("Whisper staging fixture is disabled.");
            }

            var applicationDataRoot = ApplicationDataRoot.Resolve();
            if (!IsWithinFixtureRoot(applicationDataRoot, "DCX_WHISPER_STAGING_FIXTURE_ROOT"))
            {
                throw new InvalidOperationException("Whisper staging fixture storage is outside its root.");
            }
            runtimeLease = ApplicationDataOperationLease.Acquire(
                applicationDataRoot,
                "starting the Whisper staging fixture");
            var canonicalArchive = Path.GetFullPath(archivePath);
            var archive = new FileInfo(canonicalArchive);
            if (!archive.Exists || archive.Length is <= 0 or > 1024L * 1024 * 1024 ||
                !string.Equals(archive.Extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Whisper staging fixture archive is unavailable.");
            }
            RejectFixtureReparseChain(canonicalArchive);
            using var stream = new FileStream(
                canonicalArchive,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            var candidate = new PinnedLocalModelDependencyCandidate(
                "synthetic-whisper-runtime",
                LocalModelDependencyKind.WhisperRuntimeArchive,
                "Synthetic Whisper runtime staging fixture",
                new Uri("https://github.com/ggml-org/whisper.cpp/releases/download/test/runtime.zip"),
                archive.Length,
                digest,
                new LocalModelDependencyLicense(
                    "Synthetic MIT fixture with contained notice",
                    new Uri("https://raw.githubusercontent.com/ggml-org/whisper.cpp/test/LICENSE"),
                    100,
                    new string('a', 64),
                    true),
                []);
            var catalog = new LocalModelDependencyCatalogReview(
                LocalModelDependencyCatalogLoader.CurrentSchemaVersion,
                "dcx-p06-staging-fixture",
                digest,
                Array.AsReadOnly([candidate]));
            var modelStore = LocalModelSelectionStore.OpenAsync(applicationDataRoot)
                .GetAwaiter().GetResult();
            var whisperStore = WhisperBackendSelectionStore.OpenAsync(applicationDataRoot)
                .GetAwaiter().GetResult();
            var ffmpegStore = FfmpegBackendSelectionStore.OpenAsync(applicationDataRoot)
                .GetAwaiter().GetResult();
            MainWindow = new LocalModelsWindow(
                modelStore,
                whisperStore,
                ffmpegStore,
                applicationDataRoot,
                catalog,
                timeProvider: null);
            MainWindow.Show();
        }
        catch
        {
            runtimeLease?.Dispose();
            runtimeLease = null;
            Shutdown(2);
        }
    }

    private static bool IsWithinFixtureRoot(string path, string environmentVariable)
    {
        var configuredRoot = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot)) return false;
        try
        {
            var root = Path.GetFullPath(configuredRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(root) || string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void RejectFixtureReparseChain(string path)
    {
        for (var current = new FileInfo(path).Directory; current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Whisper staging fixture path crosses a reparse point.");
            }
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("Whisper staging fixture archive is a reparse point.");
        }
    }

    private sealed class SyntheticPackageTrustVerifier : IPackageInstallTrustVerifier
    {
        public Task<PackageInstallTrustEvidence> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PackageInstallTrustEvidence(
                SignatureValid: true,
                PackageIdentityName: "DCx.AgenticBrowser.ValidationFixture",
                PublisherId: "CN=DCx Synthetic Validation Fixture",
                Version: "1.2.3.4",
                SignerCertificateSha256: new string('a', 64)));
        }
    }
}
