using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using DCx.Core;
using DCx.Core.Browsing;
using DCx.Core.Downloads;
using DCx.Core.Navigation;
using DCx.Core.Profiles;
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
    private readonly Dictionary<string, Task<CoreWebView2Environment>> _environments = new(StringComparer.Ordinal);
    private readonly ObservableCollection<BrowserHistoryEntry> _historyItems = [];
    private readonly ObservableCollection<BrowserBookmark> _bookmarkItems = [];
    private readonly ObservableCollection<string> _downloadItems = [];
    private int _focusRegion;

    public MainWindow()
    {
        var metadataPaths = new BrowserMetadataPaths();
        _historyStore = new BrowserHistoryStore(metadataPaths.HistoryFile);
        _bookmarkStore = new BrowserBookmarkStore(metadataPaths.BookmarksFile);

        InitializeComponent();
        ProfilesBox.ItemsSource = BrowserProfileCatalog.BuiltInProfiles;
        ProfilesBox.SelectedItem = ProductIdentity.DefaultBrowserProfileName;
        HistoryList.ItemsSource = _historyItems;
        BookmarksList.ItemsSource = _bookmarkItems;
        DownloadsList.ItemsSource = _downloadItems;
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
            ConfigureBrowser(state);
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

    private void ConfigureBrowser(BrowserTabState tab)
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
    }

    private void OnNavigationStarting(BrowserTabState tab, CoreWebView2NavigationStartingEventArgs args)
    {
        var decision = _navigationPolicy.Evaluate(args.Uri);
        if (!decision.IsAllowed)
        {
            args.Cancel = true;
            SetStatus(decision.RejectionReason ?? "Navigation was blocked.");
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

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        public string ProfileName { get; } = profileName;

        public WebView2 Browser { get; } = browser;

        public TabItem TabItem { get; } = tabItem;

        public TextBlock TitleText { get; } = titleText;

        public Button CloseButton { get; } = closeButton;

        public bool IsLoading { get; set; }
    }
}
