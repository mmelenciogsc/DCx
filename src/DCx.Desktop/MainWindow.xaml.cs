using System.IO;
using System.Windows;
using System.Windows.Input;
using DCx.Core;
using DCx.Core.Navigation;
using DCx.Core.Profiles;
using Microsoft.Web.WebView2.Core;

namespace DCx.Desktop;

public partial class MainWindow : Window
{
    private readonly AddressNavigationPolicy _navigationPolicy = new();
    private readonly BrowserProfilePaths _profilePaths = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileDirectory = _profilePaths.GetProfileDirectory(ProductIdentity.DefaultBrowserProfileName);
            Directory.CreateDirectory(profileDirectory);

            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            Browser.CoreWebView2.DocumentTitleChanged += OnDocumentTitleChanged;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = true;

            AddressBox.Text = "https://example.com";
            NavigateFromAddressBar();
        }
        catch (Exception exception)
        {
            SetStatus($"Browser startup failed. {exception.Message}");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Browser.Dispose();
    }

    private void OnGoClick(object sender, RoutedEventArgs e) => NavigateFromAddressBar();

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (Browser.CanGoBack)
        {
            Browser.GoBack();
        }
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBar();
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left && Browser.CanGoBack)
        {
            Browser.GoBack();
            e.Handled = true;
        }
    }

    private void NavigateFromAddressBar()
    {
        if (Browser.CoreWebView2 is null)
        {
            SetStatus("The browser is still starting.");
            return;
        }

        var decision = _navigationPolicy.Evaluate(AddressBox.Text);
        if (!decision.IsAllowed || decision.Uri is null)
        {
            SetStatus(decision.RejectionReason ?? "Navigation was blocked.");
            return;
        }

        Browser.CoreWebView2.Navigate(decision.Uri.AbsoluteUri);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        SetStatus($"Loading {GetSafeHost(e.Uri)}.");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BackButton.IsEnabled = Browser.CanGoBack;
        if (Browser.Source is not null)
        {
            AddressBox.Text = Browser.Source.AbsoluteUri;
        }

        SetStatus(e.IsSuccess ? "Page loaded." : $"Page load failed. Web error {e.WebErrorStatus}.");
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        var documentTitle = Browser.CoreWebView2?.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(documentTitle)
            ? ProductIdentity.ProductName
            : $"{documentTitle} — {ProductIdentity.ProductName}";
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private static string GetSafeHost(string rawUri)
    {
        return Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "the requested page";
    }
}
