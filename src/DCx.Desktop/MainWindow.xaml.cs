using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DCx.Core.Automation;
using DCx.Core;
using DCx.Core.Browsing;
using DCx.Core.Control;
using DCx.Core.Downloads;
using DCx.Core.Deployment;
using DCx.Core.Finance;
using DCx.Core.GlobalKnowledge;
using DCx.Core.LocalModels;
using DCx.Core.Navigation;
using DCx.Core.Profiles;
using DCx.Core.Reliability;
using DCx.Core.Social;
using DCx.Core.Storage;
using DCx.Desktop.Automation;
using DCx.Desktop.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop;

public partial class MainWindow : Window, IFacebookHistoricalPostBrowserHost
{
    private static readonly Uri StartPage = new("https://example.com/");

    private readonly IWebView2RuntimeProbe _webView2RuntimeProbe;
    private readonly IWebView2PrerequisiteHandoff _webView2PrerequisiteHandoff;
    private readonly IGlobalRegistryEnrollmentStore _globalRegistryEnrollmentStore;
    private readonly AddressNavigationPolicy _navigationPolicy = new();
    private readonly BrowserProfilePaths _profilePaths = new();
    private readonly BrowserHistoryStore _historyStore;
    private readonly BrowserBookmarkStore _bookmarkStore;
    private readonly DownloadPathPolicy _downloadPathPolicy = new();
    private readonly BrowserControlCoordinator _controlCoordinator = new();
    private readonly BrowserDomAdapter _domAdapter;
    private readonly FacebookHistoricalPostPageInspector _facebookPostPageInspector = new();
    private WebVideoFrameDescriptionService? _webVideoFrameDescriptionService;
    private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
    private readonly ObservableCollection<BrowserHistoryEntry> _historyItems = [];
    private readonly ObservableCollection<BrowserBookmark> _bookmarkItems = [];
    private readonly ObservableCollection<string> _downloadItems = [];
    private readonly ObservableCollection<string> _agentLogItems = [];
    private readonly bool _offlineStartup = OfflineStartupPolicy.IsEnabled(
        Environment.GetEnvironmentVariable(OfflineStartupPolicy.EnvironmentVariable));
    private int _focusRegion;
    private AgentDemoStage _agentDemoStage;
    private AgentControlRequest? _agentRequest;
    private AgentActionLease? _agentLease;
    private BrowserTabState? _agentTab;
    private Uri? _agentTarget;
    private NativeAppControlWindow? _nativeAppControlWindow;
    private FacebookHistoricalPostSearchWindow? _facebookPostSearchWindow;
    private OpenAiControlWindow? _openAiControlWindow;
    private LocalModelsWindow? _localModelsWindow;
    private ContentViewerWindow? _contentViewerWindow;
    private ContributionsWindow? _contributionsWindow;
    private GlobalKnowledgeWindow? _globalKnowledgeWindow;
    private bool _globalKnowledgeOpening;
    private CareRecipientsWindow? _careRecipientsWindow;
    private CareOperationsWindow? _careOperationsWindow;
    private CareContributorDashboardWindow? _careContributorDashboardWindow;
    private CrashDiagnosticsWindow? _crashDiagnosticsWindow;
    private ControlledUpdateWindow? _controlledUpdateWindow;
    private Task<LocalModelSelectionStore>? _localModelSelectionStore;
    private Task<OllamaRuntimeSelectionStore>? _ollamaRuntimeSelectionStore;
    private Task<OllamaApprovedRuntimeHost>? _ollamaRuntimeHost;
    private Task<WhisperBackendSelectionStore>? _whisperBackendSelectionStore;
    private Task<FfmpegBackendSelectionStore>? _ffmpegBackendSelectionStore;
    private Task<PayoutRecipientSnapshotStore>? _payoutRecipientSnapshotStore;
    private BrowserTabState? _pausedWebVideoTab;
    private Uri? _pausedWebVideoPage;
    private bool _browserStartupInProgress;

    public MainWindow()
        : this(
            new InstalledWebView2RuntimeProbe(),
            new WindowsGlobalRegistryEnrollmentStore(),
            new WindowsWebView2PrerequisiteHandoff())
    {
    }

    internal MainWindow(IWebView2RuntimeProbe webView2RuntimeProbe)
        : this(
            webView2RuntimeProbe,
            new WindowsGlobalRegistryEnrollmentStore(),
            new WindowsWebView2PrerequisiteHandoff())
    {
    }

    internal MainWindow(
        IWebView2RuntimeProbe webView2RuntimeProbe,
        IGlobalRegistryEnrollmentStore globalRegistryEnrollmentStore,
        IWebView2PrerequisiteHandoff? webView2PrerequisiteHandoff = null)
    {
        _webView2RuntimeProbe = webView2RuntimeProbe ?? throw new ArgumentNullException(nameof(webView2RuntimeProbe));
        _webView2PrerequisiteHandoff = webView2PrerequisiteHandoff ?? new WindowsWebView2PrerequisiteHandoff();
        _globalRegistryEnrollmentStore = globalRegistryEnrollmentStore ??
            throw new ArgumentNullException(nameof(globalRegistryEnrollmentStore));
        _domAdapter = new BrowserDomAdapter(_controlCoordinator);
        var metadataPaths = new BrowserMetadataPaths();
        _historyStore = new BrowserHistoryStore(metadataPaths.HistoryFile);
        _bookmarkStore = new BrowserBookmarkStore(metadataPaths.BookmarksFile);

        InitializeComponent();
        ProfilesBox.ItemsSource = BrowserProfileCatalog.BuiltInProfiles;
        ProfilesBox.SelectedItem = ProductIdentity.DefaultBrowserProfileName;
        HistoryList.ItemsSource = _historyItems;
        BookmarksList.ItemsSource = _bookmarkItems;
        DownloadsList.ItemsSource = _downloadItems;
        AgentActionLog.ItemsSource = _agentLogItems;
        _controlCoordinator.ModeChanged += OnControlModeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private BrowserTabState? CurrentTab => (BrowserTabs.SelectedItem as TabItem)?.Tag as BrowserTabState;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ReplaceItems(_historyItems, await _historyStore.ReadAsync());
            ReplaceItems(_bookmarkItems, await _bookmarkStore.ReadAsync());
            await StartBrowserIfAvailableAsync();
        }
        catch
        {
            ShowWebView2Prerequisite(inspectFailed: true);
        }
    }

    private async Task StartBrowserIfAvailableAsync()
    {
        if (_browserStartupInProgress || BrowserTabs.Items.Count > 0) return;
        _browserStartupInProgress = true;
        try
        {
            WebView2RuntimeStatus runtime;
            try
            {
                runtime = _webView2RuntimeProbe.Inspect();
            }
            catch
            {
                ShowWebView2Prerequisite(inspectFailed: true);
                return;
            }

            if (!runtime.IsAvailable)
            {
                ShowWebView2Prerequisite(inspectFailed: false);
                return;
            }

            HideWebView2Prerequisite();
            try
            {
                await CreateTabAsync(GetSelectedProfile(), GetNewTabAddress());
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowWebView2Prerequisite(inspectFailed: false);
            }
        }
        finally
        {
            _browserStartupInProgress = false;
        }
    }

    private void ShowWebView2Prerequisite(bool inspectFailed)
    {
        BrowserTabs.Visibility = Visibility.Collapsed;
        WebView2PrerequisitePanel.Visibility = Visibility.Visible;
        AddressBox.IsEnabled = false;
        GoButton.IsEnabled = false;
        NewTabButton.IsEnabled = false;
        ProfilesBox.IsEnabled = false;
        BookmarkButton.IsEnabled = false;
        AgentDrawerButton.IsEnabled = false;
        FacebookPostFinderButton.IsEnabled = false;
        OpenWebView2PageApprovalBox.IsChecked = false;
        OpenWebView2DownloadPageButton.IsEnabled = false;
        WebView2PrerequisiteExplanation.Text = inspectFailed
            ? "DCx could not verify Microsoft Edge WebView2 Runtime, so it did not open a web page. DCx will not download or run an installer. You can separately approve opening Microsoft's official page in your default browser; DCx verifies that the Runtime is still missing before handoff. Install or repair the Evergreen Runtime yourself, then return here and choose Recheck."
            : "DCx cannot open web pages because Microsoft Edge WebView2 Runtime is unavailable. DCx will not download or run an installer. You can separately approve opening Microsoft's official page in your default browser, install the Evergreen Runtime yourself, then return here and choose Recheck.";
        SetStatus(inspectFailed
            ? "WebView2 Runtime could not be verified. Browser commands are unavailable. No download or installation was attempted."
            : "WebView2 Runtime is unavailable. Browser commands are unavailable. No download or installation was attempted.");
        WebView2PrerequisiteHeading.Focus();
    }

    private void HideWebView2Prerequisite()
    {
        WebView2PrerequisitePanel.Visibility = Visibility.Collapsed;
        BrowserTabs.Visibility = Visibility.Visible;
        AddressBox.IsEnabled = true;
        GoButton.IsEnabled = true;
        NewTabButton.IsEnabled = true;
        ProfilesBox.IsEnabled = true;
        BookmarkButton.IsEnabled = true;
        AgentDrawerButton.IsEnabled = true;
        FacebookPostFinderButton.IsEnabled = true;
        OpenWebView2PageApprovalBox.IsChecked = false;
        OpenWebView2DownloadPageButton.IsEnabled = false;
    }

    private void OnCopyWebView2DownloadAddressClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(WebView2PrerequisiteHandoffPolicy.OfficialConsumerPageAddress);
            SetStatus("The official Microsoft WebView2 address was copied. DCx did not open a page or download software.");
        }
        catch
        {
            SetStatus("The official address could not be copied. Select the read-only address and press Control C instead.");
        }
    }

    private void OnWebView2PageApprovalChanged(object sender, RoutedEventArgs e)
    {
        OpenWebView2DownloadPageButton.IsEnabled =
            WebView2PrerequisitePanel.Visibility == Visibility.Visible &&
            OpenWebView2PageApprovalBox.IsChecked == true;
    }

    private void OnOpenWebView2DownloadPageClick(object sender, RoutedEventArgs e)
    {
        OpenWebView2DownloadPageButton.IsEnabled = false;
        try
        {
            WebView2RuntimeStatus runtime;
            try
            {
                runtime = _webView2RuntimeProbe.Inspect();
            }
            catch
            {
                SetStatus("The WebView2 Runtime state could not be verified. The official page was not opened.");
                return;
            }

            var decision = WebView2PrerequisiteHandoffPolicy.Evaluate(
                runtimeVerifiedMissing: !runtime.IsAvailable,
                exactHandoffApproved: OpenWebView2PageApprovalBox.IsChecked == true);
            if (!decision.Permitted)
            {
                SetStatus(runtime.IsAvailable
                    ? "WebView2 Runtime is now available. The official page was not opened. Choose Recheck to enable browsing."
                    : "The official page was not opened. Check the separate approval first.");
                return;
            }

            _webView2PrerequisiteHandoff.OpenOfficialConsumerPage();
            SetStatus(
                "The official Microsoft WebView2 page was handed to the default browser. " +
                "DCx did not download or run an installer. After installing the Evergreen Runtime yourself, return and choose Recheck.");
        }
        catch
        {
            SetStatus("The official Microsoft WebView2 page could not be opened. DCx did not download or run an installer.");
        }
        finally
        {
            OpenWebView2PageApprovalBox.IsChecked = false;
            OpenWebView2DownloadPageButton.IsEnabled = false;
        }
    }

    private async void OnRecheckWebView2RuntimeClick(object sender, RoutedEventArgs e)
    {
        RecheckWebView2RuntimeButton.IsEnabled = false;
        SetStatus("Checking WebView2 Runtime on this device. No download or installation will occur.");
        try
        {
            await StartBrowserIfAvailableAsync();
        }
        catch
        {
            ShowWebView2Prerequisite(inspectFailed: true);
        }
        finally
        {
            RecheckWebView2RuntimeButton.IsEnabled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var tab in GetTabs())
        {
            tab.Browser.Dispose();
        }
        if (_ollamaRuntimeHost is { IsCompletedSuccessfully: true })
        {
            _ollamaRuntimeHost.Result.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task CreateTabAsync(string profileName, Uri? address = null)
    {
        if (!BrowserProfileCatalog.IsBuiltIn(profileName))
        {
            throw new InvalidOperationException("The selected browser profile is not supported.");
        }

        var browser = new WebView2();
        AutomationProperties.SetAutomationId(browser, $"WebView-{Guid.NewGuid():N}");
        AutomationProperties.SetName(browser, $"Interactive web page, {profileName} profile");
        AutomationProperties.SetHelpText(browser, "The live web page shared by you and approved agent actions.");

        var titleText = new TextBlock
        {
            Text = "New tab",
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var closeButton = new Button
        {
            Content = "Close",
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 52,
        };
        AutomationProperties.SetName(closeButton, "Close new tab");
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(titleText);
        header.Children.Add(closeButton);

        var tabItem = new TabItem { Header = header, Content = browser };
        var state = new BrowserTabState(profileName, browser, tabItem, titleText, closeButton);
        tabItem.Tag = state;
        AutomationProperties.SetName(tabItem, $"New tab, {profileName} profile");
        closeButton.Click += (_, _) => CloseTab(state);

        BrowserTabs.Items.Add(tabItem);
        BrowserTabs.SelectedItem = tabItem;
        SetStatus($"Starting a new tab in the {profileName} profile.");

        try
        {
            var environment = await GetEnvironmentAsync(profileName);
            InstalledWebView2RuntimeProbe.RequireStableVersion(environment.BrowserVersionString);
            await browser.EnsureCoreWebView2Async(environment);
            await ConfigureBrowserAsync(state);
            if (address is null && _offlineStartup)
            {
                browser.NavigateToString(OfflineStartupPolicy.DocumentHtml);
            }
            else
            {
                browser.CoreWebView2.Navigate((address ?? StartPage).AbsoluteUri);
            }
        }
        catch
        {
            BrowserTabs.Items.Remove(tabItem);
            browser.Dispose();
            throw;
        }
    }

    private Task<CoreWebView2Environment> GetEnvironmentAsync(string profileName)
    {
        if (!_environments.TryGetValue(profileName, out var environment))
        {
            var profileDirectory = _profilePaths.GetProfileDirectory(profileName);
            Directory.CreateDirectory(profileDirectory);
            environment = CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profileDirectory,
                options: InstalledWebView2RuntimeProbe.CreateStableOptions());
            _environments.Add(profileName, environment);
        }

        return environment;
    }

    private async Task ConfigureBrowserAsync(BrowserTabState tab)
    {
        var core = tab.Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
        core.NavigationStarting += (_, args) => OnNavigationStarting(tab, args);
        core.NavigationCompleted += async (_, args) => await OnNavigationCompletedAsync(tab, args);
        core.DocumentTitleChanged += (_, _) => OnDocumentTitleChanged(tab);
        core.DownloadStarting += (_, args) => OnDownloadStarting(tab, args);
        core.NewWindowRequested += (_, args) => OnNewWindowRequested(tab, args);
        core.WebMessageReceived += (_, args) => OnBrowserWebMessageReceived(args);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "document.addEventListener('keydown',()=>window.chrome.webview.postMessage('dcx:user-input'),true);" +
            "document.addEventListener('pointerdown',()=>window.chrome.webview.postMessage('dcx:user-input'),true);");
    }

    private void OnNavigationStarting(BrowserTabState tab, CoreWebView2NavigationStartingEventArgs args)
    {
        if (ReferenceEquals(tab, _pausedWebVideoTab))
        {
            _pausedWebVideoTab = null;
            _pausedWebVideoPage = null;
        }

        tab.NavigationGeneration++;
        if (_controlCoordinator.Mode == BrowserControlMode.Agent &&
            (_agentDemoStage != AgentDemoStage.Navigating || !ReferenceEquals(tab, _agentTab)))
        {
            _controlCoordinator.NotifyPhysicalUserInput("Unexpected navigation revoked the active agent lease.");
        }

        var isOfflineDocument = _offlineStartup && tab.NavigationGeneration == 1 &&
                                OfflineStartupPolicy.IsInitialInternalNavigation(args.Uri);
        var decision = isOfflineDocument
            ? new NavigationDecision(true, new Uri(OfflineStartupPolicy.DocumentAddress), null)
            : _navigationPolicy.Evaluate(args.Uri);
        if (!decision.IsAllowed)
        {
            args.Cancel = true;
            SetStatus(decision.RejectionReason ?? "Navigation was blocked.");
            if (_agentDemoStage == AgentDemoStage.Navigating && ReferenceEquals(tab, _agentTab))
            {
                CancelAgentDemo("Approved agent navigation failed.");
            }

            return;
        }

        tab.IsLoading = true;
        UpdateNavigationControls();
        if (CurrentTab == tab)
        {
            SetStatus($"Loading {decision.Uri?.Host ?? "the requested page"} in the {tab.ProfileName} profile.");
        }
    }

    private async Task OnNavigationCompletedAsync(BrowserTabState tab, CoreWebView2NavigationCompletedEventArgs args)
    {
        tab.IsLoading = false;
        if (CurrentTab == tab)
        {
            UpdateAddressAndNavigationControls();
        }

        if (!args.IsSuccess || tab.Browser.Source is null)
        {
            if (CurrentTab == tab)
            {
                SetStatus($"Page load failed. Web error {args.WebErrorStatus}.");
            }

            return;
        }

        if (_offlineStartup && OfflineStartupPolicy.IsOfflineDocument(tab.Browser.Source.AbsoluteUri))
        {
            if (CurrentTab == tab)
            {
                SetStatus("DCx is ready offline. No website was contacted during startup.");
            }

            return;
        }

        try
        {
            var title = tab.Browser.CoreWebView2.DocumentTitle;
            var updated = await _historyStore.RecordAsync(tab.Browser.Source, title, DateTimeOffset.UtcNow);
            ReplaceItems(_historyItems, updated);
            if (CurrentTab == tab)
            {
                SetStatus($"Page loaded in the {tab.ProfileName} profile.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Page loaded, but history could not be saved. {exception.Message}");
        }

        if (_agentDemoStage == AgentDemoStage.Navigating && ReferenceEquals(tab, _agentTab))
        {
            await ContinueAgentDemoAfterNavigationAsync(tab);
        }
    }

    private void OnDocumentTitleChanged(BrowserTabState tab)
    {
        var title = tab.Browser.CoreWebView2.DocumentTitle;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = tab.Browser.Source?.Host ?? "New tab";
        }

        tab.TitleText.Text = title;
        AutomationProperties.SetName(tab.TabItem, $"{title}, {tab.ProfileName} profile");
        AutomationProperties.SetName(tab.CloseButton, $"Close {title} tab");
        if (CurrentTab == tab)
        {
            Title = $"{title} — {ProductIdentity.ProductName}";
        }
    }

    private void OnDownloadStarting(BrowserTabState tab, CoreWebView2DownloadStartingEventArgs args)
    {
        var configuredDownloadRoot = Environment.GetEnvironmentVariable("DCX_DOWNLOAD_ROOT");
        var downloadsRoot = string.IsNullOrWhiteSpace(configuredDownloadRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                ProductIdentity.CompanyDataDirectoryName)
            : Path.GetFullPath(configuredDownloadRoot.Trim());
        Directory.CreateDirectory(downloadsRoot);

        var target = _downloadPathPolicy.GetAvailablePath(downloadsRoot, args.ResultFilePath);
        args.ResultFilePath = target;
        args.Handled = true;
        var item = $"Starting {Path.GetFileName(target)} — {tab.ProfileName} profile";
        _downloadItems.Insert(0, item);
        SetStatus($"Download started: {Path.GetFileName(target)}. Saved under Downloads, DCx.");

        args.DownloadOperation.StateChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            var updated = $"{Path.GetFileName(target)} — {args.DownloadOperation.State}";
            var index = _downloadItems.IndexOf(item);
            if (index >= 0)
            {
                _downloadItems[index] = updated;
            }

            if (args.DownloadOperation.State != CoreWebView2DownloadState.InProgress)
            {
                SetStatus($"Download {args.DownloadOperation.State.ToString().ToLowerInvariant()}: {Path.GetFileName(target)}.");
            }
        });
    }

    private async void OnNewWindowRequested(BrowserTabState tab, CoreWebView2NewWindowRequestedEventArgs args)
    {
        if (_controlCoordinator.Mode is BrowserControlMode.Agent or BrowserControlMode.PausedForNavigation)
        {
            args.Handled = true;
            _controlCoordinator.NotifyPhysicalUserInput("An unapproved popup revoked the agent lease.");
            AddAgentLog("Blocked an unapproved popup request.");
            return;
        }

        var decision = _navigationPolicy.Evaluate(args.Uri);
        if (!decision.IsAllowed || decision.Uri is null)
        {
            args.Handled = true;
            SetStatus(decision.RejectionReason ?? "The requested new window was blocked.");
            return;
        }

        args.Handled = true;
        await CreateTabAsync(tab.ProfileName, decision.Uri);
    }

    private void OnBrowserWebMessageReceived(CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.TryGetWebMessageAsString() == "dcx:user-input")
            {
                RegisterPhysicalUserInput("User input inside the live page revoked the agent lease.");
            }
        }
        catch (ArgumentException)
        {
            // Non-string messages are untrusted page data and are intentionally ignored.
        }
    }

    private void NavigateFromAddressBar()
    {
        var tab = CurrentTab;
        if (tab?.Browser.CoreWebView2 is null)
        {
            SetStatus("The selected browser tab is still starting.");
            return;
        }

        var decision = _navigationPolicy.Evaluate(AddressBox.Text);
        if (!decision.IsAllowed || decision.Uri is null)
        {
            SetStatus(decision.RejectionReason ?? "Navigation was blocked.");
            return;
        }

        tab.Browser.CoreWebView2.Navigate(decision.Uri.AbsoluteUri);
    }

    private void CloseTab(BrowserTabState tab)
    {
        if (ReferenceEquals(tab, _pausedWebVideoTab))
        {
            _pausedWebVideoTab = null;
            _pausedWebVideoPage = null;
        }

        var index = BrowserTabs.Items.IndexOf(tab.TabItem);
        BrowserTabs.Items.Remove(tab.TabItem);
        tab.Browser.Dispose();

        if (BrowserTabs.Items.Count == 0)
        {
            _ = CreateTabAsync(GetSelectedProfile(), GetNewTabAddress());
        }
        else
        {
            BrowserTabs.SelectedIndex = Math.Clamp(index, 0, BrowserTabs.Items.Count - 1);
            SetStatus("Tab closed.");
        }
    }

    private async void OnBookmarkClick(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab?.Browser.Source is null)
        {
            SetStatus("There is no loaded page to bookmark.");
            return;
        }

        try
        {
            var updated = await _bookmarkStore.AddAsync(
                tab.Browser.Source,
                tab.Browser.CoreWebView2?.DocumentTitle,
                DateTimeOffset.UtcNow);
            ReplaceItems(_bookmarkItems, updated);
            SetStatus("Current page bookmarked.");
        }
        catch (Exception exception)
        {
            SetStatus($"The bookmark could not be saved. {exception.Message}");
        }
    }

    private void OnBookmarksOpen(object sender, MouseButtonEventArgs e) => NavigateSelectedBookmark();

    private void OnBookmarksKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateSelectedBookmark();
            e.Handled = true;
        }
    }

    private void NavigateSelectedBookmark()
    {
        if (BookmarksList.SelectedItem is BrowserBookmark bookmark && Uri.TryCreate(bookmark.Address, UriKind.Absolute, out var uri))
        {
            NavigateCurrentTab(uri);
        }
    }

    private void OnHistoryOpen(object sender, MouseButtonEventArgs e) => NavigateSelectedHistory();

    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateSelectedHistory();
            e.Handled = true;
        }
    }

    private void NavigateSelectedHistory()
    {
        if (HistoryList.SelectedItem is BrowserHistoryEntry history && Uri.TryCreate(history.Address, UriKind.Absolute, out var uri))
        {
            NavigateCurrentTab(uri);
        }
    }

    private void NavigateCurrentTab(Uri address)
    {
        var tab = CurrentTab;
        if (tab?.Browser.CoreWebView2 is null)
        {
            SetStatus("The selected browser tab is still starting.");
            return;
        }

        CloseUtilityPanel();
        tab.Browser.CoreWebView2.Navigate(address.AbsoluteUri);
    }

    private async void OnNewTabClick(object sender, RoutedEventArgs e) =>
        await CreateTabAsync(GetSelectedProfile(), GetNewTabAddress());

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (CurrentTab?.Browser.CanGoBack == true)
        {
            CurrentTab.Browser.GoBack();
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (CurrentTab?.Browser.CanGoForward == true)
        {
            CurrentTab.Browser.GoForward();
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab?.Browser.CoreWebView2 is null)
        {
            return;
        }

        if (tab.IsLoading)
        {
            tab.Browser.CoreWebView2.Stop();
            SetStatus("Page loading stopped.");
        }
        else
        {
            tab.Browser.Reload();
        }
    }

    private void OnGoClick(object sender, RoutedEventArgs e) => NavigateFromAddressBar();

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBar();
            e.Handled = true;
        }
    }

    private void OnBrowserTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, BrowserTabs))
        {
            return;
        }

        RegisterPhysicalUserInput("User changed the selected browser tab and revoked the agent lease.");

        UpdateAddressAndNavigationControls();
        var tab = CurrentTab;
        if (tab is not null)
        {
            var title = tab.Browser.CoreWebView2?.DocumentTitle;
            Title = string.IsNullOrWhiteSpace(title)
                ? ProductIdentity.ProductName
                : $"{title} — {ProductIdentity.ProductName}";
            SetStatus($"Selected {title ?? "tab"}, {tab.ProfileName} profile.");
        }
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            SetStatus($"New tabs will use the {GetSelectedProfile()} profile.");
        }
    }

    private void OnShowBookmarksClick(object sender, RoutedEventArgs e) => ShowUtilityPanel(0, BookmarksList);

    private void OnShowHistoryClick(object sender, RoutedEventArgs e) => ShowUtilityPanel(1, HistoryList);

    private void OnShowDownloadsClick(object sender, RoutedEventArgs e) => ShowUtilityPanel(2, DownloadsList);

    private void ShowUtilityPanel(int tabIndex, Control focusTarget)
    {
        UtilityPanel.Visibility = Visibility.Visible;
        UtilityTabs.SelectedIndex = tabIndex;
        focusTarget.Focus();
    }

    private void OnCloseUtilityPanelClick(object sender, RoutedEventArgs e) => CloseUtilityPanel();

    private void CloseUtilityPanel()
    {
        UtilityPanel.Visibility = Visibility.Collapsed;
        BrowserTabs.Focus();
    }

    private void OnRequestAgentDemoClick(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        var decision = _navigationPolicy.Evaluate(AgentTargetBox.Text);
        if (tab?.Browser.CoreWebView2 is null || !decision.IsAllowed || decision.Uri is null)
        {
            SetStatus(decision.RejectionReason ?? "A ready browser tab and valid controlled URL are required.");
            return;
        }

        if (!decision.Uri.IsLoopback)
        {
            SetStatus("The deterministic P02 handoff demo is restricted to a loopback fixture URL.");
            return;
        }

        try
        {
            _agentTab = tab;
            _agentTarget = decision.Uri;
            _agentRequest = _controlCoordinator.RequestAgentControl(
                "Navigate the controlled fixture, pause for user input, then validate and continue.",
                decision.Uri);
            _agentLease = null;
            _agentDemoStage = AgentDemoStage.AwaitingApproval;
            AgentApprovalText.Text =
                $"Allow the deterministic demo to control only this tab at {BrowserOrigin.FromUri(decision.Uri)}? " +
                "It will navigate, inspect control names without reading values, focus the Approval code field, pause for you, and continue only after handback.";
            AgentApprovalPanel.Visibility = Visibility.Visible;
            ApproveAgentButton.Focus();
            AddAgentLog($"Requested control for origin {BrowserOrigin.FromUri(decision.Uri)}.");
        }
        catch (Exception exception)
        {
            SetStatus($"Agent-control request failed. {exception.Message}");
        }
    }

    private void OnApproveAgentClick(object sender, RoutedEventArgs e)
    {
        if (_agentRequest is null || _agentTab is null || _agentTarget is null || CurrentTab != _agentTab)
        {
            CancelAgentDemo("The approved browser tab is no longer selected.");
            return;
        }

        try
        {
            _agentLease = _controlCoordinator.ApproveAgentControl(
                _agentRequest.RequestId,
                _agentTab.TabId,
                _agentTab.NavigationGeneration);
            if (!_controlCoordinator.IsNavigationAllowed(
                    _agentLease.LeaseId,
                    _agentTab.TabId,
                    _agentTarget,
                    out var rejectionReason))
            {
                throw new InvalidOperationException(rejectionReason);
            }

            _agentDemoStage = AgentDemoStage.Navigating;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            _controlCoordinator.PauseForApprovedNavigation(
                _agentLease.LeaseId,
                _agentTab.TabId,
                _agentTarget);
            AddAgentLog("User approved. Navigating within the approved origin.");
            _agentTab.Browser.CoreWebView2.Navigate(_agentTarget.AbsoluteUri);
        }
        catch (Exception exception)
        {
            CancelAgentDemo($"Approval could not start the demo. {exception.Message}");
        }
    }

    private void OnRejectAgentClick(object sender, RoutedEventArgs e) =>
        CancelAgentDemo("User rejected the agent-control request.");

    private async Task ContinueAgentDemoAfterNavigationAsync(BrowserTabState tab)
    {
        if (tab.Browser.Source is null)
        {
            CancelAgentDemo("The controlled page has no valid address.");
            return;
        }

        try
        {
            _agentLease = _controlCoordinator.ResumeAfterApprovedNavigation(
                tab.TabId,
                tab.Browser.Source,
                tab.NavigationGeneration);
            var snapshot = await _domAdapter.ReadSnapshotAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration);
            AddAgentLog($"Inspected {snapshot.Count} named/actionable DOM elements without reading input values.");

            tab.Browser.Focus();
            var focusResult = await _domAdapter.ExecuteAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration,
                new BrowserDomAction(BrowserDomActionKind.FocusById, "approval-code"));
            if (!focusResult.Succeeded)
            {
                throw new InvalidOperationException(focusResult.Error ?? "The controlled input could not be focused.");
            }

            _agentDemoStage = AgentDemoStage.AwaitingUser;
            _controlCoordinator.YieldToUser("Agent paused for manual user input on the approved page.");
            AgentUserInstruction.Text =
                "Your control. Type any non-secret fixture value in Approval code. Then activate Hand back to agent. The agent cannot continue until you do.";
            AddAgentLog("Paused. User control restored for manual field entry.");
        }
        catch (Exception exception)
        {
            CancelAgentDemo($"Agent inspection stopped safely. {exception.Message}");
        }
    }

    private void OnTakeControlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_controlCoordinator.Mode == BrowserControlMode.AgentPending)
            {
                CancelAgentDemo("User cancelled the pending control request.");
            }
            else if (_controlCoordinator.Mode is BrowserControlMode.Agent or BrowserControlMode.PausedForNavigation)
            {
                _agentDemoStage = AgentDemoStage.AwaitingUser;
                _controlCoordinator.YieldToUser("User explicitly took control.");
                AgentUserInstruction.Text = "Your control. Hand back only when you want the approved demo to continue.";
                AddAgentLog("User explicitly took control; active lease revoked.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Takeover failed safely. {exception.Message}");
        }
    }

    private async void OnHandBackClick(object sender, RoutedEventArgs e)
    {
        var tab = _agentTab;
        if (tab?.Browser.Source is null || _agentDemoStage != AgentDemoStage.AwaitingUser)
        {
            SetStatus("There is no paused agent step ready for handback.");
            return;
        }

        try
        {
            _agentLease = _controlCoordinator.HandBackToAgent(
                tab.TabId,
                tab.Browser.Source,
                tab.NavigationGeneration);
            AddAgentLog("User handed control back; issued a new scoped lease.");

            var readResult = await _domAdapter.ExecuteAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration,
                new BrowserDomAction(BrowserDomActionKind.ReadValueById, "approval-code"));
            if (!readResult.Succeeded || string.IsNullOrWhiteSpace(readResult.Value))
            {
                await _domAdapter.ExecuteAsync(
                    tab.Browser,
                    _agentLease,
                    tab.TabId,
                    tab.NavigationGeneration,
                    new BrowserDomAction(BrowserDomActionKind.FocusById, "approval-code"));
                _controlCoordinator.YieldToUser("The fixture field was empty. User control restored.");
                AgentUserInstruction.Text = "Approval code is empty. Enter a non-secret fixture value, then hand back again.";
                AddAgentLog("Validation found an empty field; no click occurred and user control was restored.");
                return;
            }

            var clickResult = await _domAdapter.ExecuteAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration,
                new BrowserDomAction(BrowserDomActionKind.ClickById, "continue"));
            if (!clickResult.Succeeded)
            {
                throw new InvalidOperationException(clickResult.Error ?? "The controlled Continue action failed.");
            }

            var finalSnapshot = await _domAdapter.ReadSnapshotAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration);
            if (!finalSnapshot.Any(element =>
                    element.Id == "result" &&
                    element.Name == "Agent resumed after user handback."))
            {
                throw new InvalidOperationException("The page did not expose the expected completion evidence.");
            }

            tab.Browser.Focus();
            await _domAdapter.ExecuteAsync(
                tab.Browser,
                _agentLease,
                tab.TabId,
                tab.NavigationGeneration,
                new BrowserDomAction(BrowserDomActionKind.FocusById, "approval-code"));
            _controlCoordinator.CompleteAgentWork("Controlled agent task completed; user control and field focus restored.");
            _agentDemoStage = AgentDemoStage.Completed;
            AgentUserInstruction.Text = "Demo complete. You have control. Focus returned to Approval code.";
            AddAgentLog("Validated page evidence, completed the approved action, and returned control to the user.");
        }
        catch (Exception exception)
        {
            CancelAgentDemo($"Agent continuation stopped safely. {exception.Message}");
        }
    }

    private void OnCancelAgentClick(object sender, RoutedEventArgs e) =>
        CancelAgentDemo("User cancelled the agent task.");

    private void CancelAgentDemo(string reason)
    {
        _controlCoordinator.RejectOrCancel(reason);
        _agentDemoStage = AgentDemoStage.Cancelled;
        _agentLease = null;
        AgentApprovalPanel.Visibility = Visibility.Collapsed;
        AgentUserInstruction.Text = reason;
        AddAgentLog(reason);
    }

    private void OnControlModeChanged(object? sender, BrowserControlChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            AgentControlModeText.Text = $"Control: {FormatMode(e.CurrentMode)}. {e.Reason}";
            TakeControlButton.IsEnabled = e.CurrentMode is BrowserControlMode.Agent or BrowserControlMode.PausedForNavigation;
            HandBackButton.IsEnabled = e.CurrentMode == BrowserControlMode.UserTakeover &&
                                       _agentDemoStage == AgentDemoStage.AwaitingUser;
            CancelAgentButton.IsEnabled = e.CurrentMode is not BrowserControlMode.User and not BrowserControlMode.Cancelled;
            RequestAgentDemoButton.IsEnabled = e.CurrentMode is BrowserControlMode.User or BrowserControlMode.Cancelled;
            AgentTargetBox.IsEnabled = RequestAgentDemoButton.IsEnabled;
            SetStatus(e.Reason);
        });
    }

    private void AddAgentLog(string message)
    {
        _agentLogItems.Insert(0, $"{DateTimeOffset.Now:T} — {message}");
        while (_agentLogItems.Count > 100)
        {
            _agentLogItems.RemoveAt(_agentLogItems.Count - 1);
        }
    }

    private static string FormatMode(BrowserControlMode mode) => mode switch
    {
        BrowserControlMode.User => "User",
        BrowserControlMode.AgentPending => "Approval pending",
        BrowserControlMode.Agent => "Agent",
        BrowserControlMode.UserTakeover => "User takeover",
        BrowserControlMode.PausedForNavigation => "Agent paused for navigation",
        BrowserControlMode.Cancelled => "Cancelled",
        _ => mode.ToString(),
    };

    private void OnAgentDrawerClick(object sender, RoutedEventArgs e)
    {
        var opening = AgentDrawer.Visibility != Visibility.Visible;
        AgentDrawer.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        if (opening)
        {
            CloseAgentDrawerButton.Focus();
            SetStatus("Agent controls opened. Agent execution is unavailable until P02 is validated.");
        }
        else
        {
            AgentDrawerButton.Focus();
            SetStatus("Agent controls closed.");
        }
    }

    private void OnWindowsAppClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening Windows application controls.");
        try
        {
            if (_nativeAppControlWindow is { IsVisible: true })
            {
                _nativeAppControlWindow.Activate();
                return;
            }

            _nativeAppControlWindow = new NativeAppControlWindow(ResolveRepositoryRoot())
            {
                Owner = this,
            };
            _nativeAppControlWindow.Closed += (_, _) => _nativeAppControlWindow = null;
            _nativeAppControlWindow.Show();
        }
        catch (Exception exception)
        {
            SetStatus($"Windows application controls could not open. {exception.Message}");
        }
    }

    private void OnFacebookPostFinderClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening the manual-sign-in old Facebook post finder.");
        try
        {
            if (_facebookPostSearchWindow is { IsVisible: true })
            {
                _facebookPostSearchWindow.Activate();
                return;
            }

            var site = ResolveFacebookPostSearchSite();
            SetStatus("Old-post workflow site boundary resolved. Opening its accessible window.");
            _facebookPostSearchWindow = new FacebookHistoricalPostSearchWindow(
                this,
                site)
            {
                Owner = this,
            };
            SetStatus("Old-post workflow window constructed. Showing it now.");
            _facebookPostSearchWindow.Closed += (_, _) => _facebookPostSearchWindow = null;
            _facebookPostSearchWindow.Show();
            SetStatus("Old-post workflow window shown. Manual sign-in remains under user control.");
        }
        catch (Exception exception)
        {
            SetStatus($"The old Facebook post finder could not open. {exception.Message}");
        }
    }

    void IFacebookHistoricalPostBrowserHost.NavigateSharedBrowser(Uri address)
    {
        var tab = CurrentTab;
        var decision = _navigationPolicy.Evaluate(address.AbsoluteUri);
        if (tab?.Browser.CoreWebView2 is null || !decision.IsAllowed || decision.Uri is null)
        {
            throw new InvalidOperationException(decision.RejectionReason ?? "A ready shared browser tab is required.");
        }

        tab.Browser.CoreWebView2.Navigate(decision.Uri.AbsoluteUri);
        SetStatus("Shared browser navigation started for the old-post workflow.");
    }

    void IFacebookHistoricalPostBrowserHost.FocusSharedBrowser()
    {
        Activate();
        CurrentTab?.Browser.Focus();
    }

    async Task<FacebookPostPageEvidence> IFacebookHistoricalPostBrowserHost.InspectCurrentPostPageAsync(
        CancellationToken cancellationToken)
    {
        var tab = CurrentTab ?? throw new InvalidOperationException("No shared browser tab is selected.");
        var address = tab.Browser.Source ?? throw new InvalidOperationException("The selected browser page has no address.");
        var generation = tab.NavigationGeneration;
        var evidence = await _facebookPostPageInspector.InspectAsync(tab.Browser, address, cancellationToken);
        if (!ReferenceEquals(CurrentTab, tab) ||
            tab.NavigationGeneration != generation ||
            tab.Browser.Source != address)
        {
            throw new InvalidOperationException("The shared browser page changed during inspection.");
        }

        return evidence;
    }

    private static FacebookPostSearchSite ResolveFacebookPostSearchSite()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DCX_P12_FACEBOOK_FIXTURE"),
                "1",
                StringComparison.Ordinal))
        {
            return FacebookPostSearchSite.Production;
        }

        var origin = Environment.GetEnvironmentVariable("DCX_P12_FACEBOOK_FIXTURE_ORIGIN");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var fixtureOrigin))
        {
            throw new InvalidOperationException("The controlled Facebook-search fixture origin is invalid.");
        }

        return FacebookPostSearchSite.CreateControlledFixture(fixtureOrigin);
    }

    private void OnOpenAiClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening OpenAI and local Codex settings.");
        try
        {
            if (_openAiControlWindow is { IsVisible: true })
            {
                _openAiControlWindow.Activate();
                return;
            }

            _openAiControlWindow = new OpenAiControlWindow(ResolveRepositoryRoot())
            {
                Owner = this,
            };
            _openAiControlWindow.Closed += (_, _) => _openAiControlWindow = null;
            _openAiControlWindow.Show();
        }
        catch (Exception exception)
        {
            SetStatus($"OpenAI and local Codex settings could not open. {exception.Message}");
        }
    }

    private async void OnLocalModelsClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening installed local model settings. Nothing will be downloaded automatically.");
        try
        {
            if (_localModelsWindow is { IsVisible: true })
            {
                _localModelsWindow.Activate();
                return;
            }

            _localModelsWindow = new LocalModelsWindow(
                await GetLocalModelSelectionStoreAsync(),
                await GetOllamaRuntimeSelectionStoreAsync(),
                await GetWhisperBackendSelectionStoreAsync(),
                await GetFfmpegBackendSelectionStoreAsync(),
                await GetOllamaRuntimeHostAsync())
            {
                Owner = this,
            };
            _localModelsWindow.Closed += (_, _) => _localModelsWindow = null;
            _localModelsWindow.Show();
        }
        catch
        {
            SetStatus("Local model settings could not open safely. No selection changed and nothing was downloaded.");
        }
    }

    private async void OnContentViewerClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening files and media viewer.");
        if (_contentViewerWindow is { IsVisible: true })
        {
            _contentViewerWindow.Activate();
            return;
        }

        try
        {
            _contentViewerWindow = new ContentViewerWindow(
                await GetLocalModelSelectionStoreAsync(),
                await GetOllamaRuntimeHostAsync(),
                await GetWhisperBackendSelectionStoreAsync(),
                await GetFfmpegBackendSelectionStoreAsync())
            {
                Owner = this,
            };
        }
        catch
        {
            SetStatus("Files and media viewer could not open because local model settings are unavailable.");
            return;
        }
        _contentViewerWindow.YouTubeNavigationRequested += async (_, uri) =>
        {
            await CreateTabAsync(GetSelectedProfile(), uri);
            Activate();
        };
        _contentViewerWindow.WebVideoFrameQuestionRequested = DescribeActiveWebVideoAsync;
        _contentViewerWindow.WebVideoResumeRequested = ResumePausedWebVideoAsync;
        _contentViewerWindow.Closed += (_, _) => _contentViewerWindow = null;
        _contentViewerWindow.Show();
    }

    private async Task<WebVideoFrameDescriptionResult> DescribeActiveWebVideoAsync(
        string? question,
        CancellationToken cancellationToken)
    {
        var tab = CurrentTab;
        if (tab?.Browser.CoreWebView2 is null || tab.Browser.Source is null)
        {
            throw new WebVideoFrameException("web_video_page_unsupported");
        }

        var selectionStore = await GetLocalModelSelectionStoreAsync();
        var binding = (await selectionStore.GetStateAsync(cancellationToken)).Vision;
        if (binding is null)
        {
            throw new WebVideoFrameException("local_vision_model_not_selected");
        }

        _pausedWebVideoTab = tab;
        _pausedWebVideoPage = tab.Browser.Source;
        var service = _webVideoFrameDescriptionService ??=
            new WebVideoFrameDescriptionService(
                await GetOllamaRuntimeHostAsync());
        var result = await service.DescribeAsync(
            tab.Browser,
            binding.Name,
            binding.Digest,
            binding.LicenseSha256,
            question,
            cancellationToken);
        _pausedWebVideoPage = result.PageUri;
        return result;
    }

    private async Task ResumePausedWebVideoAsync(CancellationToken cancellationToken)
    {
        var tab = _pausedWebVideoTab;
        var page = _pausedWebVideoPage;
        if (tab is null || page is null || !BrowserTabs.Items.Contains(tab.TabItem))
        {
            throw new WebVideoFrameException("web_video_page_changed");
        }

        var service = _webVideoFrameDescriptionService ??=
            new WebVideoFrameDescriptionService(
                await GetOllamaRuntimeHostAsync());
        await service.ResumeAsync(tab.Browser, page, cancellationToken);
        _pausedWebVideoTab = null;
        _pausedWebVideoPage = null;
    }

    private void OnContributionsClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening local contribution review. Nothing is uploaded automatically.");
        if (_contributionsWindow is { IsVisible: true })
        {
            _contributionsWindow.Activate();
            return;
        }

        _contributionsWindow = new ContributionsWindow { Owner = this };
        _contributionsWindow.Closed += (_, _) => _contributionsWindow = null;
        _contributionsWindow.Show();
    }

    private async void OnGlobalKnowledgeClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening the separate read-only global knowledge replica. No synchronization will occur automatically.");
        if (_globalKnowledgeWindow is { IsVisible: true })
        {
            _globalKnowledgeWindow.Activate();
            return;
        }
        if (_globalKnowledgeOpening)
        {
            SetStatus("Global knowledge is already opening.");
            return;
        }

        _globalKnowledgeOpening = true;
        GlobalKnowledgeButton.IsEnabled = false;
        GlobalKnowledgeSourceConfiguration? configuration = null;
        try
        {
            configuration = await GlobalKnowledgeSourceFactory.CreateAsync(
                ApplicationDataRoot.Resolve(),
                _globalRegistryEnrollmentStore);
            _globalKnowledgeWindow = new GlobalKnowledgeWindow(
                configuration.Source,
                configuration.IsEnrolled
                    ? GlobalKnowledgeSourceKind.ConfiguredReplica
                    : GlobalKnowledgeSourceKind.Unavailable,
                _globalRegistryEnrollmentStore)
            {
                Owner = this,
            };
        }
        catch
        {
            if (configuration?.Source is IDisposable disposable)
            {
                disposable.Dispose();
            }
            _globalKnowledgeWindow = new GlobalKnowledgeWindow(
                new UnavailableGlobalKnowledgeViewSource(),
                GlobalKnowledgeSourceKind.EnrollmentInvalid,
                _globalRegistryEnrollmentStore)
            {
                Owner = this,
            };
            SetStatus("Protected global registry enrollment could not be opened safely. No remote state was inferred.");
        }
        finally
        {
            _globalKnowledgeOpening = false;
            GlobalKnowledgeButton.IsEnabled = true;
        }
        _globalKnowledgeWindow.Closed += (_, _) => _globalKnowledgeWindow = null;
        _globalKnowledgeWindow.Show();
    }

    private async void OnCareRecipientsClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening protected CARE payout recipient snapshots. No PayPal request will occur.");
        if (_careRecipientsWindow is { IsVisible: true })
        {
            _careRecipientsWindow.Activate();
            return;
        }

        try
        {
            _careRecipientsWindow = new CareRecipientsWindow(
                await GetPayoutRecipientSnapshotStoreAsync())
            {
                Owner = this,
            };
            _careRecipientsWindow.Closed += (_, _) => _careRecipientsWindow = null;
            _careRecipientsWindow.Show();
        }
        catch
        {
            SetStatus("CARE payout recipient snapshots could not open safely. No recipient changed and no PayPal request occurred.");
        }
    }

    private void OnCareOperationsClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening privacy-minimized CARE verified-event operations. No provider request will occur.");
        if (_careOperationsWindow is { IsVisible: true })
        {
            _careOperationsWindow.Activate();
            return;
        }

        try
        {
            var operations = CareOperationsSourceFactory.Create();
            _careOperationsWindow = new CareOperationsWindow(
                operations.Source,
                operations.IsFixture)
            {
                Owner = this,
            };
            _careOperationsWindow.Closed += (_, _) => _careOperationsWindow = null;
            _careOperationsWindow.Show();
        }
        catch
        {
            SetStatus("CARE verified-event operations could not open safely. No state changed and no provider request occurred.");
        }
    }

    private void OnCareContributorDashboardClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening the read-only CARE contributor dashboard. No payout action will occur.");
        if (_careContributorDashboardWindow is { IsVisible: true })
        {
            _careContributorDashboardWindow.Activate();
            return;
        }

        try
        {
            var dashboard = CareContributorDashboardSourceFactory.Create();
            _careContributorDashboardWindow = new CareContributorDashboardWindow(
                dashboard.Source,
                dashboard.IsFixture)
            {
                Owner = this,
            };
            _careContributorDashboardWindow.Closed +=
                (_, _) => _careContributorDashboardWindow = null;
            _careContributorDashboardWindow.Show();
        }
        catch
        {
            SetStatus(
                "CARE contributor status could not open safely. No financial state was inferred and no payout action occurred.");
        }
    }

    private void OnRecoveryClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The DCx executable path is unavailable.");
            Process.Start(new ProcessStartInfo(executable, "--recovery") { UseShellExecute = true });
            SetStatus("Backup and recovery opened separately. Close the main DCx window before starting an operation.");
        }
        catch (Exception exception)
        {
            SetStatus($"Backup and recovery could not open. {exception.Message}");
        }
    }

    private void OnCrashDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening privacy and local crash diagnostics.");
        if (_crashDiagnosticsWindow is { IsVisible: true })
        {
            _crashDiagnosticsWindow.Activate();
            return;
        }

        _crashDiagnosticsWindow = new CrashDiagnosticsWindow(
            (Application.Current as App)?.CrashReportStore)
        {
            Owner = this,
        };
        _crashDiagnosticsWindow.Closed += (_, _) => _crashDiagnosticsWindow = null;
        _crashDiagnosticsWindow.Show();
    }

    private void OnControlledUpdatesClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening controlled updates. No production update service or installer is configured.");
        if (_controlledUpdateWindow is { IsVisible: true })
        {
            _controlledUpdateWindow.Activate();
            return;
        }

        var currentVersion = typeof(ProductIdentity).Assembly.GetName().Version?.ToString(4)
            ?? "1.0.0.0";
        _controlledUpdateWindow = new ControlledUpdateWindow(
            new UnavailableControlledUpdateReviewSource(currentVersion))
        {
            Owner = this,
        };
        _controlledUpdateWindow.Closed += (_, _) => _controlledUpdateWindow = null;
        _controlledUpdateWindow.Show();
    }

    private void OnReadinessClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The DCx executable path is unavailable.");
            Process.Start(new ProcessStartInfo(executable, "--readiness") { UseShellExecute = true });
            SetStatus("The read-only release readiness dashboard opened separately.");
        }
        catch (Exception exception)
        {
            SetStatus($"The release readiness dashboard could not open. {exception.Message}");
        }
    }

    private static string ResolveRepositoryRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DCX_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return File.Exists(Path.Combine(candidate, "DCx.slnx")) ? candidate : AppContext.BaseDirectory;
    }

    private Task<LocalModelSelectionStore> GetLocalModelSelectionStoreAsync() =>
        _localModelSelectionStore ??= LocalModelSelectionStore.OpenAsync(ApplicationDataRoot.Resolve());

    private Task<OllamaRuntimeSelectionStore> GetOllamaRuntimeSelectionStoreAsync() =>
        _ollamaRuntimeSelectionStore ??=
            OllamaRuntimeSelectionStore.OpenAsync(ApplicationDataRoot.Resolve());

    private Task<OllamaApprovedRuntimeHost> GetOllamaRuntimeHostAsync() =>
        _ollamaRuntimeHost ??= CreateOllamaRuntimeHostAsync();

    private async Task<OllamaApprovedRuntimeHost> CreateOllamaRuntimeHostAsync() =>
        new(await GetOllamaRuntimeSelectionStoreAsync().ConfigureAwait(false));

    private Task<WhisperBackendSelectionStore> GetWhisperBackendSelectionStoreAsync() =>
        _whisperBackendSelectionStore ??= WhisperBackendSelectionStore.OpenAsync(ApplicationDataRoot.Resolve());

    private Task<FfmpegBackendSelectionStore> GetFfmpegBackendSelectionStoreAsync() =>
        _ffmpegBackendSelectionStore ??= FfmpegBackendSelectionStore.OpenAsync(ApplicationDataRoot.Resolve());

    private Task<PayoutRecipientSnapshotStore> GetPayoutRecipientSnapshotStoreAsync() =>
        _payoutRecipientSnapshotStore ??=
            PayoutRecipientSnapshotStore.OpenAsync(ApplicationDataRoot.Resolve());

    private Uri? GetNewTabAddress() => _offlineStartup ? null : StartPage;

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        RegisterPhysicalUserInput("Keyboard input revoked the agent lease.");

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T)
        {
            await CreateTabAsync(GetSelectedProfile(), GetNewTabAddress());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && CurrentTab is not null)
        {
            CloseTab(CurrentTab);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D)
        {
            OnBookmarkClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            ShowUtilityPanel(1, HistoryList);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.J)
        {
            ShowUtilityPanel(2, DownloadsList);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Tab)
        {
            SelectNextTab();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left && CurrentTab?.Browser.CanGoBack == true)
        {
            CurrentTab.Browser.GoBack();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Right && CurrentTab?.Browser.CanGoForward == true)
        {
            CurrentTab.Browser.GoForward();
            e.Handled = true;
        }
        else if (e.Key == Key.F6)
        {
            CycleFocusRegion();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && UtilityPanel.Visibility == Visibility.Visible)
        {
            CloseUtilityPanel();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && AgentDrawer.Visibility == Visibility.Visible)
        {
            OnAgentDrawerClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        RegisterPhysicalUserInput("Pointer input revoked the agent lease.");

    private void RegisterPhysicalUserInput(string reason)
    {
        if (_controlCoordinator.Mode is not BrowserControlMode.Agent and not BrowserControlMode.PausedForNavigation)
        {
            return;
        }

        if (_agentDemoStage is not AgentDemoStage.None and not AgentDemoStage.Completed and not AgentDemoStage.Cancelled)
        {
            _agentDemoStage = AgentDemoStage.AwaitingUser;
        }

        if (_controlCoordinator.NotifyPhysicalUserInput(reason))
        {
            AgentUserInstruction.Text = "Your control. The agent lease was revoked. Hand back only if you want the approved task to continue.";
            AddAgentLog(reason);
        }
    }

    private void SelectNextTab()
    {
        if (BrowserTabs.Items.Count == 0)
        {
            return;
        }

        BrowserTabs.SelectedIndex = (BrowserTabs.SelectedIndex + 1) % BrowserTabs.Items.Count;
    }

    private void CycleFocusRegion()
    {
        _focusRegion = (_focusRegion + 1) % 3;
        switch (_focusRegion)
        {
            case 0:
                AddressBox.Focus();
                break;
            case 1:
                CurrentTab?.Browser.Focus();
                break;
            default:
                AgentDrawerButton.Focus();
                break;
        }
    }

    private void UpdateAddressAndNavigationControls()
    {
        var tab = CurrentTab;
        if (tab?.Browser.Source is not null)
        {
            AddressBox.Text = tab.Browser.Source.AbsoluteUri;
        }

        UpdateNavigationControls();
    }

    private void UpdateNavigationControls()
    {
        var tab = CurrentTab;
        BackButton.IsEnabled = tab?.Browser.CanGoBack == true;
        ForwardButton.IsEnabled = tab?.Browser.CanGoForward == true;
        ReloadButton.IsEnabled = tab?.Browser.CoreWebView2 is not null;
        ReloadButton.Content = tab?.IsLoading == true ? "Stop" : "Reload";
        AutomationProperties.SetName(ReloadButton, tab?.IsLoading == true ? "Stop loading page" : "Reload page");
    }

    private string GetSelectedProfile() =>
        ProfilesBox.SelectedItem as string ?? ProductIdentity.DefaultBrowserProfileName;

    private IReadOnlyList<BrowserTabState> GetTabs() =>
        BrowserTabs.Items.OfType<TabItem>()
            .Select(item => item.Tag)
            .OfType<BrowserTabState>()
            .ToList();

    private void SetStatus(string message) => StatusText.Text = message;

    private static void ReplaceItems<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    private sealed class BrowserTabState(
        string profileName,
        WebView2 browser,
        TabItem tabItem,
        TextBlock titleText,
        Button closeButton)
    {
        public Guid TabId { get; } = Guid.NewGuid();

        public string ProfileName { get; } = profileName;

        public WebView2 Browser { get; } = browser;

        public TabItem TabItem { get; } = tabItem;

        public TextBlock TitleText { get; } = titleText;

        public Button CloseButton { get; } = closeButton;

        public bool IsLoading { get; set; }

        public long NavigationGeneration { get; set; }
    }

    private enum AgentDemoStage
    {
        None,
        AwaitingApproval,
        Navigating,
        AwaitingUser,
        Completed,
        Cancelled,
    }
}
