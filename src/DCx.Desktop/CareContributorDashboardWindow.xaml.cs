using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using DCx.Core.Finance;

namespace DCx.Desktop;

public partial class CareContributorDashboardWindow : Window
{
    private readonly ICareContributorDashboardSource source;
    private readonly bool isFixture;
    private readonly CancellationTokenSource lifetime = new();
    private bool closed;

    public CareContributorDashboardWindow(
        ICareContributorDashboardSource source,
        bool isFixture)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isFixture = isFixture;
        InitializeComponent();
        BoundaryText.Text = isFixture
            ? "Deterministic local validation fixture. Generated contributor data only. Provider credentials, recipient destinations, network requests, payments, payouts and production actions are unavailable."
            : "Contributor status is not configured. No allocation, hold, eligibility or payment state is inferred.";
        AutomationProperties.SetName(BoundaryText, BoundaryText.Text);
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            lifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Heading.Focus();
        await RefreshAsync();
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
                SetStatus(
                    $"Loaded {snapshot.Periods.Count} generated CARE periods. This dashboard is read-only. Network requests 0. Payout attempts 0.");
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
            $"Allocated: {FormatCents(allocated)}.{Environment.NewLine}" +
            $"Paid: {FormatCents(paid)}.{Environment.NewLine}" +
            $"Outstanding or held: {FormatCents(outstanding)}.{Environment.NewLine}" +
            $"Data source: {(isFixture ? "deterministic local fixture" : "configured contributor source")}.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        PeriodsList.ItemsSource = snapshot.Periods
            .Select(item => new ContributorPeriodListItem(item))
            .ToArray();
    }

    private void SetUnavailable()
    {
        SummaryText.Text =
            "CARE contributor status is unavailable. No empty history or zero balance is inferred.";
        AutomationProperties.SetName(SummaryText, SummaryText.Text);
        PeriodsList.ItemsSource = null;
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private static string FormatCents(decimal cents) =>
        string.Create(CultureInfo.InvariantCulture, $"USD {cents / 100:0.00}");

    private sealed class ContributorPeriodListItem(CareContributorPeriodStatus status)
    {
        public string Display { get; } =
            $"{status.PeriodId}. Period close {status.EarningPeriodClose:yyyy-MM-dd}. " +
            $"Net 60 eligibility {status.Net60EligibilityDate:yyyy-MM-dd}. " +
            $"Status {status.StatusCode}. Allocation {FormatCents(status.AllocationCents)}. " +
            $"Paid {FormatCents(status.PaidCents)}. Outstanding {FormatCents(status.OutstandingCents)}.";

        public override string ToString() => Display;
    }
}
