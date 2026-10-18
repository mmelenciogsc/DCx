using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using DCx.Core.Deployment;
using DCx.Core.Finance;
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

        if (e.Args.Length == 1 &&
            string.Equals(e.Args[0], "--dependency-transfer-fixture", StringComparison.Ordinal))
        {
            StartDependencyTransferFixture();
            return;
        }

        if (e.Args.Length == 1 &&
            string.Equals(
                e.Args[0],
                "--care-contributor-loopback-fixture",
                StringComparison.Ordinal))
        {
            StartCareContributorLoopbackFixture();
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
            var approvalAnchorStore = new FixtureLocalModelApprovalStateAnchorStore();
            var modelStore = LocalModelSelectionStore.OpenWithAnchorAsync(applicationDataRoot, approvalAnchorStore)
                .GetAwaiter().GetResult();
            var whisperStore = WhisperBackendSelectionStore.OpenWithAnchorAsync(applicationDataRoot, approvalAnchorStore)
                .GetAwaiter().GetResult();
            var ffmpegStore = FfmpegBackendSelectionStore.OpenWithAnchorAsync(applicationDataRoot, approvalAnchorStore)
                .GetAwaiter().GetResult();
            MainWindow = new LocalModelsWindow(
                modelStore,
                whisperStore,
                ffmpegStore,
                applicationDataRoot,
                catalog,
                timeProvider: null,
                stageAnchorStore: new FixtureWhisperRuntimeStageAnchorStore());
            MainWindow.Show();
        }
        catch
        {
            runtimeLease?.Dispose();
            runtimeLease = null;
            Shutdown(2);
        }
    }

    private void StartDependencyTransferFixture()
    {
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("DCX_DEPENDENCY_TRANSFER_FIXTURE"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Dependency transfer fixture is disabled.");
            }

            var applicationDataRoot = ApplicationDataRoot.Resolve();
            if (!IsWithinFixtureRoot(applicationDataRoot, "DCX_DEPENDENCY_TRANSFER_FIXTURE_ROOT"))
            {
                throw new InvalidOperationException("Dependency transfer fixture storage is outside its root.");
            }
            runtimeLease = ApplicationDataOperationLease.Acquire(
                applicationDataRoot,
                "starting the dependency transfer fixture");

            var payload = "DCx synthetic dependency transfer payload"u8.ToArray();
            var license = "DCx synthetic dependency transfer license"u8.ToArray();
            var candidate = new PinnedLocalModelDependencyCandidate(
                "synthetic-transfer-candidate",
                LocalModelDependencyKind.WhisperModel,
                "Synthetic dependency transfer candidate",
                new Uri("https://github.com/dcx/fixture/dependency.bin"),
                payload.LongLength,
                Convert.ToHexStringLower(SHA256.HashData(payload)),
                new LocalModelDependencyLicense(
                    "Synthetic fixture license",
                    new Uri("https://raw.githubusercontent.com/dcx/fixture/LICENSE"),
                    license.LongLength,
                    Convert.ToHexStringLower(SHA256.HashData(license)),
                    false),
                []);
            var catalog = new LocalModelDependencyCatalogReview(
                LocalModelDependencyCatalogLoader.CurrentSchemaVersion,
                "dcx-p06-transfer-fixture",
                Convert.ToHexStringLower(SHA256.HashData("dcx-p06-transfer-fixture"u8)),
                Array.AsReadOnly([candidate]));
            var anchorStore = new FixtureLocalModelApprovalStateAnchorStore();
            var modelStore = LocalModelSelectionStore.OpenWithAnchorAsync(applicationDataRoot, anchorStore)
                .GetAwaiter().GetResult();
            var whisperStore = WhisperBackendSelectionStore.OpenWithAnchorAsync(applicationDataRoot, anchorStore)
                .GetAwaiter().GetResult();
            var ffmpegStore = FfmpegBackendSelectionStore.OpenWithAnchorAsync(applicationDataRoot, anchorStore)
                .GetAwaiter().GetResult();
            MainWindow = new LocalModelsWindow(
                modelStore,
                whisperStore,
                ffmpegStore,
                applicationDataRoot,
                catalog,
                timeProvider: null,
                stageAnchorStore: new FixtureWhisperRuntimeStageAnchorStore(),
                enableDependencyTransfer: true,
                dependencyTransferCoordinator: new FixtureDependencyTransferSetCoordinator());
            MainWindow.Show();
        }
        catch
        {
            runtimeLease?.Dispose();
            runtimeLease = null;
            Shutdown(2);
        }
    }

    private void StartCareContributorLoopbackFixture()
    {
        const string tokenEnvironmentVariable =
            "DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_TOKEN";
        LoopbackCareContributorDashboardSource? source = null;
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(
                        "DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE"),
                    "1",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The CARE contributor loopback fixture is disabled.");
            }

            var originText = Environment.GetEnvironmentVariable(
                "DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_ORIGIN");
            var token = Environment.GetEnvironmentVariable(tokenEnvironmentVariable);
            var contributorPseudonym = Environment.GetEnvironmentVariable(
                "DCX_CARE_CONTRIBUTOR_LOOPBACK_FIXTURE_PSEUDONYM");
            if (!Uri.TryCreate(originText, UriKind.Absolute, out var origin) ||
                token is null || contributorPseudonym is null)
            {
                throw new InvalidOperationException(
                    "The CARE contributor loopback fixture configuration is incomplete.");
            }

            source = new LoopbackCareContributorDashboardSource(
                origin,
                token,
                contributorPseudonym,
                TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
            MainWindow = new CareContributorDashboardWindow(
                source,
                CareContributorDashboardSourceKind.AuthenticatedLoopbackFixture);
            MainWindow.Show();
            source = null;
        }
        catch
        {
            source?.Dispose();
            Shutdown(2);
        }
        finally
        {
            Environment.SetEnvironmentVariable(tokenEnvironmentVariable, null);
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

    private sealed class FixtureWhisperRuntimeStageAnchorStore : IWhisperRuntimeStageAnchorStore
    {
        private WhisperRuntimeStageAnchor? anchor;

        public Task<WhisperRuntimeStageAnchor?> ReadAsync(
            PinnedLocalModelDependencyCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(anchor is not null &&
                string.Equals(anchor.CandidateId, candidate.Id, StringComparison.Ordinal) &&
                string.Equals(anchor.ArchiveSha256, candidate.Sha256, StringComparison.Ordinal)
                    ? anchor
                    : null);
        }

        public Task BindAsync(
            WhisperRuntimeStageAnchor value,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchor is not null && anchor != value)
            {
                throw new InvalidOperationException("The fixture stage anchor conflicts.");
            }
            anchor = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureLocalModelApprovalStateAnchorStore : ILocalModelApprovalStateAnchorStore
    {
        private readonly Dictionary<(string Kind, string Storage), LocalModelApprovalStateAnchor> anchors = [];

        public Task<LocalModelApprovalStateAnchor?> ReadAsync(
            string stateKind,
            string storageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(anchors.TryGetValue((stateKind, storageId), out var anchor)
                ? anchor
                : null);
        }

        public Task WriteAsync(
            LocalModelApprovalStateAnchor anchor,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            anchors[(anchor.StateKind, anchor.StorageId)] = anchor;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            string stateKind,
            string storageId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            anchors.Remove((stateKind, storageId));
            return Task.CompletedTask;
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

    private sealed class FixtureDependencyTransferSetCoordinator
        : ILocalModelDependencyTransferSetCoordinator
    {
        private LocalModelDependencyTransferSetReceipt? receipt;
        private int transferAttempts;
        private bool firstRecovery = true;

        public LocalModelDependencyRedirectPolicyReview RedirectPolicyReview =>
            LocalModelDependencyRedirectPolicyReview.DenyAll;

        public async Task<LocalModelDependencyTransferSetReceipt> TransferAsync(
            LocalModelDependencyTransferPlan plan,
            LocalModelDependencyTransferSetApproval approval,
            CancellationToken cancellationToken = default,
            IProgress<LocalModelDependencyTransferSetProgress>? progress = null)
        {
            if (approval.SchemaVersion != LocalModelDependencyTransferSetApproval.CurrentSchemaVersion ||
                !approval.Approved ||
                !string.Equals(approval.PlanSha256, plan.PlanSha256, StringComparison.Ordinal) ||
                !string.Equals(
                    approval.RedirectPolicySha256,
                    RedirectPolicyReview.PolicySha256,
                    StringComparison.Ordinal))
            {
                throw new LocalModelDependencyTransferException("dependency_transfer_approval_invalid");
            }

            transferAttempts++;
            progress?.Report(new LocalModelDependencyTransferSetProgress(
                LocalModelDependencyTransferSetStage.Preparing,
                0,
                plan.Items.Count,
                0,
                0,
                0,
                plan.TransferBytes));
            long completed = 0;
            for (var index = 0; index < plan.Items.Count; index++)
            {
                var item = plan.Items[index];
                var steps = transferAttempts == 1 ? 50 : 4;
                for (var step = 1; step <= steps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(transferAttempts == 1 ? 25 : 10, cancellationToken);
                    var itemBytes = item.ExpectedBytes * step / steps;
                    progress?.Report(new LocalModelDependencyTransferSetProgress(
                        LocalModelDependencyTransferSetStage.Transferring,
                        index + 1,
                        plan.Items.Count,
                        itemBytes,
                        item.ExpectedBytes,
                        completed + itemBytes,
                        plan.TransferBytes));
                }
                completed += item.ExpectedBytes;
            }

            progress?.Report(new LocalModelDependencyTransferSetProgress(
                LocalModelDependencyTransferSetStage.ProtectingReceipt,
                plan.Items.Count,
                plan.Items.Count,
                0,
                0,
                plan.TransferBytes,
                plan.TransferBytes));
            await Task.Delay(20, cancellationToken);
            receipt = new LocalModelDependencyTransferSetReceipt(
                LocalModelDependencyTransferSetCoordinator.CurrentReceiptSchemaVersion,
                plan.CatalogId,
                plan.CatalogSha256,
                plan.PlanSha256,
                RedirectPolicyReview.PolicySha256,
                plan.TransferBytes,
                Array.AsReadOnly(plan.Items.Select((item, index) =>
                    new LocalModelDependencyTransferredFile(
                        index,
                        $"{index:D2}-{item.Sha256}.payload",
                        item.ExpectedBytes,
                        item.Sha256)).ToArray()));
            progress?.Report(new LocalModelDependencyTransferSetProgress(
                LocalModelDependencyTransferSetStage.Published,
                plan.Items.Count,
                plan.Items.Count,
                0,
                0,
                plan.TransferBytes,
                plan.TransferBytes));
            return receipt;
        }

        public async Task<LocalModelDependencyTransferSetReceipt> VerifyAsync(
            LocalModelDependencyTransferPlan plan,
            CancellationToken cancellationToken = default,
            IProgress<LocalModelDependencyTransferSetProgress>? progress = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = receipt ?? throw new InvalidOperationException("No synthetic completed set exists.");
            progress?.Report(new LocalModelDependencyTransferSetProgress(
                LocalModelDependencyTransferSetStage.Verifying,
                current.Files.Count,
                current.Files.Count,
                0,
                0,
                current.TransferBytes,
                current.TransferBytes));
            await Task.Delay(20, cancellationToken);
            return current;
        }

        public Task<int> CleanupAbandonedAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = firstRecovery ? 1 : 0;
            firstRecovery = false;
            return Task.FromResult(result);
        }

        public void Dispose()
        {
        }
    }
}
