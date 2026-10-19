using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using DCx.Core.Finance;

namespace DCx.Desktop;

internal enum CareContributorDashboardSourceKind
{
    Unavailable,
    GeneratedFixture,
    AuthenticatedLoopbackFixture,
}

public partial class CareContributorDashboardWindow : Window
{
    private readonly ICareContributorDashboardSource source;
    private readonly CareContributorDashboardSourceKind sourceKind;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;

    public CareContributorDashboardWindow(
        ICareContributorDashboardSource source,
        bool isFixture)
        : this(
            source,
            isFixture
                ? CareContributorDashboardSourceKind.GeneratedFixture
                : CareContributorDashboardSourceKind.Unavailable)
    {
    }

    internal CareContributorDashboardWindow(
        ICareContributorDashboardSource source,
        CareContributorDashboardSourceKind sourceKind)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.sourceKind = sourceKind;
        InitializeComponent();
        BoundaryText.Text = sourceKind switch
        {
            CareContributorDashboardSourceKind.GeneratedFixture =>
                "Deterministic local validation fixture. Generated contributor data only. Provider credentials, recipient destinations, network requests, payments, payouts and production actions are unavailable.",
            CareContributorDashboardSourceKind.AuthenticatedLoopbackFixture =>
                "Default-off authenticated local validation transport. Contributor state comes from the integrity-verified local CARE ledger. Static fixture credentials, loopback HTTP and generated identities are not production authentication. This window is read-only. PayPal requests, payments, payouts and production actions are unavailable.",
            _ =>
                "Contributor status is not configured. No allocation, hold, eligibility or payment state is inferred.",
        };
        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            if (source is IDisposable disposable) disposable.Dispose();
            lifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Heading.Focus();
        await RefreshAsync();
        if (!closed) Heading.Focus();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) =>
        await RefreshAsync(moveFocus: true);

    private async Task RefreshAsync(bool moveFocus = false)
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await source.LoadAsync(lifetime.Token);
            CareContributorDashboardPolicy.RequireSnapshot(snapshot);
            if (closed) return;
            if (snapshot.StatusCode == CareContributorDashboardPolicy.NotConfigured)
            {
                SetUnavailable();
                SetStatus(
                    "CARE contributor status remains unavailable. No allocation, hold, eligibility, pending or payment state was inferred.");
            }
            else
            {
                SetSnapshot(snapshot);
                SetStatus(sourceKind == CareContributorDashboardSourceKind.GeneratedFixture
                    ? $"Loaded {snapshot.Periods.Count} generated CARE periods and {FormatNoticeCount(snapshot.Notices.Count)}. This dashboard is read-only. Network requests 0. Notification transports 0. Payout attempts 0."
                    : $"Loaded {snapshot.Periods.Count} CARE periods and {FormatNoticeCount(snapshot.Notices.Count)} from the authenticated local ledger fixture. This dashboard is read-only. Provider requests 0. Notification transports 0. Payout attempts 0.");
            }

            if (moveFocus) SummaryText.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            if (!closed)
            {
                SetUnavailable();
                SetStatus(
                    "CARE contributor status failed closed. No contributor or financial state was inferred.");
            }
        }
        finally
        {
            if (!closed) RefreshButton.IsEnabled = true;
        }
    }

    private void SetSnapshot(CareContributorDashboardSnapshot snapshot)
    {
        var allocated = snapshot.Periods.Sum(item => (decimal)item.AllocationCents);
        var paid = snapshot.Periods.Sum(item => (decimal)item.PaidCents);
        var outstanding = snapshot.Periods.Sum(item => (decimal)item.OutstandingCents);
        SummaryText.Text =
            $"Contributor pseudonym: {snapshot.ContributorPseudonym}.{Environment.NewLine}" +
            $"Currency: {snapshot.Currency}.{Environment.NewLine}" +
            $"Generated UTC: {snapshot.GeneratedUtc:O}.{Environment.NewLine}" +
            $"Periods: {snapshot.Periods.Count}.{Environment.NewLine}" +
            $"Local failed-payout notices: {snapshot.Notices.Count}.{Environment.NewLine}" +
            $"Allocated: {FormatCents(allocated)}.{Environment.NewLine}" +
            $"Paid: {FormatCents(paid)}.{Environment.NewLine}" +
            $"Outstanding or held: {FormatCents(outstanding)}.{Environment.NewLine}" +
            $"Data source: {GetDataSourceLabel()}.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        PeriodsList.ItemsSource = snapshot.Periods
            .Select(item => new ContributorPeriodListItem(item))
            .ToArray();
        NoticesList.ItemsSource = snapshot.Notices
            .Select(item => new ContributorNoticeListItem(item))
            .ToArray();
    }

    private void SetUnavailable()
    {
        SummaryText.Text =
            "CARE contributor status is unavailable. No empty history or zero balance is inferred.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        PeriodsList.ItemsSource = null;
        NoticesList.ItemsSource = null;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private static string FormatCents(decimal cents) =>
        string.Create(CultureInfo.InvariantCulture, $"USD {cents / 100:0.00}");

    private static string FormatNoticeCount(int count) =>
        $"{count} local failed-payout notice {(count == 1 ? "record" : "records")}";

    private string GetDataSourceLabel() => sourceKind switch
    {
        CareContributorDashboardSourceKind.GeneratedFixture =>
            "deterministic local fixture",
        CareContributorDashboardSourceKind.AuthenticatedLoopbackFixture =>
            "authenticated local CARE ledger fixture",
        _ => "configured contributor source",
    };

    private sealed class ContributorPeriodListItem(CareContributorPeriodStatus status)
    {
        public string Display { get; } =
            $"{status.PeriodId}. Period close {status.EarningPeriodClose:yyyy-MM-dd}. " +
            $"Net 60 eligibility {status.Net60EligibilityDate:yyyy-MM-dd}. " +
            $"Status {status.StatusCode}. Allocation {FormatCents(status.AllocationCents)}. " +
            $"Paid {FormatCents(status.PaidCents)}. Outstanding {FormatCents(status.OutstandingCents)}. " +
            GetLocalNotice(status.StatusCode);

        public override string ToString() => Display;

        private static string GetLocalNotice(string statusCode) => statusCode switch
        {
            CareContributorDashboardPolicy.Withheld =>
                "Notice: This allocation is withheld. No payout is scheduled.",
            CareContributorDashboardPolicy.HeldBelowThreshold =>
                "Notice: This allocation remains held below the payout threshold.",
            CareContributorDashboardPolicy.Pending =>
                "Notice: A payout is pending. No contributor action is requested.",
            CareContributorDashboardPolicy.PartiallyPaid =>
                "Notice: Part was paid. The remaining amount stays held for review; no automatic retry occurs.",
            CareContributorDashboardPolicy.Paid =>
                "Notice: This allocation was paid.",
            _ => "Notice: No payout state is inferred.",
        };
    }

    private sealed class ContributorNoticeListItem(CareContributorLocalNotice notice)
    {
        public string Display { get; } =
            $"{notice.PeriodId}. Recorded UTC {notice.RecordedUtc:O}. " +
            $"Amount {FormatCents(notice.AmountCents)}. " +
            GetNoticeText(notice.NoticeCode) +
            " This is a local dashboard notice. No external message was sent.";

        public override string ToString() => Display;

        private static string GetNoticeText(string noticeCode) => noticeCode switch
        {
            CareContributorDashboardPolicy.FailedPayoutHoldRetained =>
                "Notice: A failed payout remains held. No automatic retry occurs.",
            CareContributorDashboardPolicy.RecipientUpdateRequested =>
                "Notice: Payout recipient information needs review. The failed amount remains held, and no automatic retry occurs.",
            CareContributorDashboardPolicy.RetryIntentCancelled =>
                "Notice: Retry intent was cancelled. The failed amount remains held.",
            _ => "Notice: No failed-payout state is inferred.",
        };
    }
}
