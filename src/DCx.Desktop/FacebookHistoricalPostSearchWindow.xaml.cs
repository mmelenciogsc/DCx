using System.Windows;
using DCx.Core.Social;

namespace DCx.Desktop;

internal interface IFacebookHistoricalPostBrowserHost
{
    void NavigateSharedBrowser(Uri address);

    void FocusSharedBrowser();

    Task<FacebookPostPageEvidence> InspectCurrentPostPageAsync(CancellationToken cancellationToken);
}

public partial class FacebookHistoricalPostSearchWindow : Window
{
    private readonly IFacebookHistoricalPostBrowserHost _browserHost;
    private readonly FacebookHistoricalPostSearchSession _session;
    private readonly CancellationTokenSource _lifetime = new();

    internal FacebookHistoricalPostSearchWindow(
        IFacebookHistoricalPostBrowserHost browserHost,
        FacebookPostSearchSite site)
    {
        _browserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        _session = new FacebookHistoricalPostSearchSession(site);
        InitializeComponent();
        BoundaryText.Text = site.IsControlledFixture
            ? "Controlled local validation fixture. This page is not Facebook, accepts no real credential, and proves only the manual-control, current-page-inspection, and no-fabricated-result boundaries."
            : "Uses only the current DCx WebView2 profile and shared live viewport. DCx never copies Chrome or Edge cookies, reads password values, or claims a result until the current direct post page is inspected. Facebook may block or change this assisted workflow.";
        Loaded += (_, _) => HeadingText.Focus();
        Closed += (_, _) =>
        {
            _session.Cancel();
            _lifetime.Cancel();
            _lifetime.Dispose();
        };
    }

    private void OnOpenLoginClick(object sender, RoutedEventArgs e)
    {
        ClearResult("No current post has been inspected.");
        _browserHost.NavigateSharedBrowser(_session.ManualLoginAddress);
        SetStatus("Shared browser opened for manual sign-in. Complete sign-in, MFA, consent, or account recovery yourself. Then return and explicitly hand back.");
        _browserHost.FocusSharedBrowser();
    }

    private void OnManualLoginAcknowledgementChanged(object sender, RoutedEventArgs e) =>
        HandBackAfterLoginButton.IsEnabled = ManualLoginCompleteCheckBox.IsChecked == true;

    private void OnHandBackAfterLoginClick(object sender, RoutedEventArgs e)
    {
        if (ManualLoginCompleteCheckBox.IsChecked != true)
        {
            SetStatus("Manual sign-in acknowledgment is required before handback.");
            return;
        }

        _session.ConfirmManualLoginHandback();
        SearchQueryBox.IsEnabled = true;
        OpenSearchButton.IsEnabled = true;
        InspectCurrentPostButton.IsEnabled = true;
        ManualLoginCompleteCheckBox.IsEnabled = false;
        HandBackAfterLoginButton.IsEnabled = false;
        SetStatus("Manual browser control was handed back. This acknowledgment does not prove authentication. Enter search terms and open the post search.");
        SearchQueryBox.Focus();
    }

    private void OnOpenSearchClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var address = _session.OpenSearch(SearchQueryBox.Text);
            ClearResult("Search opened. No post result is claimed. Review the live page and open one candidate post before inspection.");
            _browserHost.NavigateSharedBrowser(address);
            SetStatus("Post search opened in the shared browser. Review the live results and open one candidate post. DCx will not claim the search-results page as a found post.");
            _browserHost.FocusSharedBrowser();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            SetStatus($"Search was not opened. {exception.Message}");
        }
    }

    private async void OnInspectCurrentPostClick(object sender, RoutedEventArgs e)
    {
        InspectCurrentPostButton.IsEnabled = false;
        SetStatus("Inspecting bounded visible semantics from the current shared-browser page. Password values, cookies, and storage are not read.");
        try
        {
            var evidence = await _browserHost.InspectCurrentPostPageAsync(_lifetime.Token);
            RenderResult(_session.Inspect(evidence));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ClearResult("No post result was recorded because current-page inspection failed.");
            SetStatus($"Current-page inspection stopped safely. {exception.Message}");
            ResultSummaryText.Focus();
        }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                InspectCurrentPostButton.IsEnabled = _session.State != FacebookHistoricalPostSearchState.AwaitingManualLogin;
            }
        }
    }

    private void OnReportBlockedClick(object sender, RoutedEventArgs e) =>
        RenderResult(_session.ReportSiteBlocked());

    private void RenderResult(FacebookPostInspectionResult result)
    {
        if (result.HasVerifiedPost && result.DirectAddress is not null)
        {
            DirectAddressBox.Text = result.DirectAddress.AbsoluteUri;
            CopyDirectAddressButton.IsEnabled = true;
            PostDetailsText.Text =
                $"Heading: {result.Heading ?? "Unavailable"}.{Environment.NewLine}" +
                $"Published: {result.PublishedText ?? result.PublishedDateTime ?? "Unavailable"}.{Environment.NewLine}" +
                $"Visible excerpt: {result.Excerpt ?? "Unavailable"}.";
            MediaLabelsList.ItemsSource = result.MediaLabels;
            ResultSummaryText.Text = "One current direct post was inspected in the shared viewport. Review the untrusted visible text and address before copying it.";
            SetStatus("Inspected current post verified. The address comes only from the current shared-browser location, not page-supplied canonical metadata.");
        }
        else
        {
            var summary = result.State switch
            {
                FacebookHistoricalPostSearchState.ManualLoginRequired =>
                    "The current page requires manual sign-in. No post result or address was recorded.",
                FacebookHistoricalPostSearchState.SiteBlocked =>
                    "Facebook blocked, challenged, or changed the assisted flow. Continue manually. No post result or address was fabricated.",
                _ => "The current page is not an inspected direct post. Open one candidate post and try again. No result or address was recorded.",
            };
            ClearResult(summary);
            SetStatus(summary);
        }

        ResultSummaryText.Focus();
    }

    private void ClearResult(string summary)
    {
        DirectAddressBox.Clear();
        CopyDirectAddressButton.IsEnabled = false;
        PostDetailsText.Text = string.Empty;
        MediaLabelsList.ItemsSource = Array.Empty<string>();
        ResultSummaryText.Text = summary;
    }

    private void OnCopyDirectAddressClick(object sender, RoutedEventArgs e)
    {
        if (!CopyDirectAddressButton.IsEnabled || string.IsNullOrWhiteSpace(DirectAddressBox.Text)) return;
        Clipboard.SetText(DirectAddressBox.Text);
        SetStatus("The inspected direct post address was copied. No post text, account data, or credential was copied.");
    }

    private void OnReturnToBrowserClick(object sender, RoutedEventArgs e) => _browserHost.FocusSharedBrowser();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}
