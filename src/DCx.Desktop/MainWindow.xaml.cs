using System.Collections.ObjectModel;
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
using DCx.Core.Navigation;
using DCx.Core.Profiles;
using DCx.Desktop.Automation;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DCx.Desktop;

public partial class MainWindow : Window
{
    private static readonly Uri StartPage = new("https://example.com/");

    private readonly AddressNavigationPolicy _navigationPolicy = new();
    private readonly BrowserProfilePaths _profilePaths = new();
    private readonly BrowserHistoryStore _historyStore;
    private readonly BrowserBookmarkStore _bookmarkStore;
    private readonly DownloadPathPolicy _downloadPathPolicy = new();
    private readonly BrowserControlCoordinator _controlCoordinator = new();
    private readonly BrowserDomAdapter _domAdapter;
    private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
    private readonly ObservableCollection<BrowserHistoryEntry> _historyItems = [];
    private readonly ObservableCollection<BrowserBookmark> _bookmarkItems = [];
    private readonly ObservableCollection<string> _downloadItems = [];
    private readonly ObservableCollection<string> _agentLogItems = [];
    private int _focusRegion;
    private AgentDemoStage _agentDemoStage;
    private AgentControlRequest? _agentRequest;
    private AgentActionLease? _agentLease;
    private BrowserTabState? _agentTab;
    private Uri? _agentTarget;
    private NativeAppControlWindow? _nativeAppControlWindow;
    private OpenAiControlWindow? _openAiControlWindow;
    private ContentViewerWindow? _contentViewerWindow;
    private ContributionsWindow? _contributionsWindow;

    public MainWindow()
    {
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
            await CreateTabAsync(GetSelectedProfile(), StartPage);
        }
        catch (Exception exception)
        {
            SetStatus($"Browser startup failed. {exception.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        foreach (var tab in GetTabs())
        {
            tab.Browser.Dispose();
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
            await browser.EnsureCoreWebView2Async(environment);
            await ConfigureBrowserAsync(state);
            browser.CoreWebView2.Navigate((address ?? StartPage).AbsoluteUri);
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
            environment = CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
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
        tab.NavigationGeneration++;
        if (_controlCoordinator.Mode == BrowserControlMode.Agent &&
            (_agentDemoStage != AgentDemoStage.Navigating || !ReferenceEquals(tab, _agentTab)))
        {
            _controlCoordinator.NotifyPhysicalUserInput("Unexpected navigation revoked the active agent lease.");
        }

        var decision = _navigationPolicy.Evaluate(args.Uri);
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
        var index = BrowserTabs.Items.IndexOf(tab.TabItem);
        BrowserTabs.Items.Remove(tab.TabItem);
        tab.Browser.Dispose();

        if (BrowserTabs.Items.Count == 0)
        {
            _ = CreateTabAsync(GetSelectedProfile(), StartPage);
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
        await CreateTabAsync(GetSelectedProfile(), StartPage);

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

    private void OnContentViewerClick(object sender, RoutedEventArgs e)
    {
        SetStatus("Opening files and media viewer.");
        if (_contentViewerWindow is { IsVisible: true })
        {
            _contentViewerWindow.Activate();
            return;
        }

        _contentViewerWindow = new ContentViewerWindow { Owner = this };
        _contentViewerWindow.YouTubeNavigationRequested += async (_, uri) =>
        {
            await CreateTabAsync(GetSelectedProfile(), uri);
            Activate();
        };
        _contentViewerWindow.Closed += (_, _) => _contentViewerWindow = null;
        _contentViewerWindow.Show();
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
            await CreateTabAsync(GetSelectedProfile(), StartPage);
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
