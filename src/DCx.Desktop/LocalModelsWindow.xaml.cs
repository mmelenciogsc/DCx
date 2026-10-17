using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DCx.Core.LocalModels;

namespace DCx.Desktop;

public partial class LocalModelsWindow : Window
{
    private static readonly Uri OllamaOrigin = new("http://localhost:11434/");
    private readonly LocalModelSelectionStore store;
    private readonly CancellationTokenSource lifetime = new();
    private CandidateRow? reviewedCandidate;
    private string? reviewedLicenseSha256;

    public LocalModelsWindow(LocalModelSelectionStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => lifetime.Cancel();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApprovalBox.IsChecked = false;
        reviewedCandidate = null;
        reviewedLicenseSha256 = null;
        UseVisionButton.IsEnabled = false;
        UseEmbeddingButton.IsEnabled = false;
        SetLicense("Select an installed model to inspect its complete license text.");
        if (ModelsList.SelectedItem is not CandidateRow selected)
        {
            SetDetails("Select an installed model to review it.");
            return;
        }

        SetDetails($"Reviewing {selected.Descriptor.Name}.");
        try
        {
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var inspection = await client.InspectModelAsync(selected.Descriptor.Name, lifetime.Token);
            if (ModelsList.SelectedItem is not CandidateRow current || !ReferenceEquals(current, selected)) return;
            if (string.IsNullOrWhiteSpace(inspection.License))
            {
                SetDetails($"{selected.Display} License metadata is missing, so this model cannot be selected.");
                SetLicense("No license text was returned for this installed model.");
                return;
            }

            reviewedCandidate = selected;
            reviewedLicenseSha256 = Sha256(inspection.License);
            SetLicense(inspection.License);
            var licenseSummary = inspection.License
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "License text present";
            SetDetails(
                $"{selected.Display} License: {licenseSummary}. License SHA-256: {reviewedLicenseSha256}. " +
                (selected.EligibleVision || selected.EligibleEmbedding
                    ? "This exact installed identity is eligible for the listed capability. Check approval before selecting it."
                    : "This identity is not eligible because its name is mutable, duplicated, or does not advertise vision or embedding capability."));
            UseVisionButton.IsEnabled = selected.EligibleVision;
            UseEmbeddingButton.IsEnabled = selected.EligibleEmbedding;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetDetails("The selected model metadata could not be inspected safely. No selection changed.");
            SetLicense("The selected model license text could not be inspected safely.");
        }
    }

    private async void OnUseVisionClick(object sender, RoutedEventArgs e) =>
        await ApproveAsync(isVision: true);

    private async void OnUseEmbeddingClick(object sender, RoutedEventArgs e) =>
        await ApproveAsync(isVision: false);

    private async void OnClearVisionClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await store.ClearVisionAsync(lifetime.Token);
            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus("The local vision-model selection was cleared. No model was downloaded, deleted, or contacted remotely.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The local vision-model selection could not be cleared. No model or user content was changed.");
        }
    }

    private async void OnClearEmbeddingClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await store.ClearEmbeddingAsync(lifetime.Token);
            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus("The local embedding-model selection was cleared. No model was downloaded, deleted, or contacted remotely.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The local embedding-model selection could not be cleared. No model or user content was changed.");
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        ApprovalBox.IsChecked = false;
        reviewedCandidate = null;
        reviewedLicenseSha256 = null;
        SetDetails("Select an installed model to review it.");
        SetLicense("Select an installed model to inspect its complete license text.");
        SetControlsEnabled(false);
        SetStatus("Reading installed model identities from the loopback Ollama runtime. Nothing is downloaded or uploaded.");
        try
        {
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var models = await client.ListModelsAsync(lifetime.Token);
            var counts = models
                .GroupBy(model => model.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var rows = models
                .OrderBy(model => model.Name, StringComparer.Ordinal)
                .ThenBy(model => model.Digest, StringComparer.Ordinal)
                .Select(model => new CandidateRow(
                    model,
                    counts[model.Name] == 1 && model.HasDigestBoundName && model.SupportsVision,
                    counts[model.Name] == 1 && model.HasDigestBoundName && model.SupportsEmbeddings))
                .ToArray();
            ModelsList.ItemsSource = rows;
            await UpdateCurrentSelectionsAsync();
            var eligible = rows.Count(row => row.EligibleVision || row.EligibleEmbedding);
            SetStatus(
                $"Found {rows.Length} installed model records. {eligible} unique digest-bound records are eligible. Nothing was downloaded or uploaded.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ModelsList.ItemsSource = null;
            SetStatus("Installed local models could not be read safely. No selection changed and no remote provider was used.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task ApproveAsync(bool isVision)
    {
        if (ApprovalBox.IsChecked != true || reviewedCandidate is null || reviewedLicenseSha256 is null)
        {
            SetStatus("Selection was not saved. Review a model and check the explicit approval box first.");
            ApprovalBox.Focus();
            return;
        }

        if (isVision && !reviewedCandidate.EligibleVision ||
            !isVision && !reviewedCandidate.EligibleEmbedding)
        {
            SetStatus("Selection was not saved because this model identity is not eligible for that capability.");
            return;
        }

        SetControlsEnabled(false);
        try
        {
            using var client = new OllamaLocalClient(OllamaOrigin, TimeSpan.FromSeconds(30));
            var inventory = await client.ListModelsAsync(lifetime.Token);
            var sameName = inventory.Where(candidate =>
                    string.Equals(candidate.Name, reviewedCandidate.Descriptor.Name, StringComparison.Ordinal))
                .ToArray();
            if (sameName.Length != 1 ||
                !string.Equals(
                    sameName[0].Digest,
                    reviewedCandidate.Descriptor.Digest,
                    StringComparison.OrdinalIgnoreCase) ||
                !sameName[0].HasDigestBoundName ||
                isVision && !sameName[0].SupportsVision || !isVision && !sameName[0].SupportsEmbeddings)
            {
                throw new InvalidOperationException("The reviewed model binding changed.");
            }

            var inspection = await client.InspectModelAsync(sameName[0].Name, lifetime.Token);
            if (string.IsNullOrWhiteSpace(inspection.License) ||
                !string.Equals(Sha256(inspection.License), reviewedLicenseSha256, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The reviewed model license changed.");
            }

            if (isVision)
            {
                await store.SetVisionAsync(
                    sameName[0], reviewedLicenseSha256, true, DateTimeOffset.UtcNow, lifetime.Token);
            }
            else
            {
                await store.SetEmbeddingAsync(
                    sameName[0], reviewedLicenseSha256, true, DateTimeOffset.UtcNow, lifetime.Token);
            }

            ApprovalBox.IsChecked = false;
            await UpdateCurrentSelectionsAsync();
            SetStatus(
                $"The exact digest-bound local {(isVision ? "vision" : "embedding")} model selection was saved. Nothing was downloaded or uploaded.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            SetStatus("The model identity or license could not be revalidated, so no selection was saved.");
        }
        finally
        {
            SetControlsEnabled(true);
        }
    }

    private async Task UpdateCurrentSelectionsAsync()
    {
        var state = await store.GetStateAsync(lifetime.Token);
        SetCurrent(CurrentVisionText, "Vision", state.Vision);
        SetCurrent(CurrentEmbeddingText, "Embedding", state.Embedding);
    }

    private void SetControlsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        ModelsList.IsEnabled = enabled;
        ApprovalBox.IsEnabled = enabled;
        UseVisionButton.IsEnabled = enabled && reviewedCandidate?.EligibleVision == true;
        UseEmbeddingButton.IsEnabled = enabled && reviewedCandidate?.EligibleEmbedding == true;
    }

    private static void SetCurrent(TextBlock target, string capability, ApprovedLocalModelBinding? binding)
    {
        var value = binding is null
            ? $"{capability}: none selected."
            : $"{capability}: {binding.Name}. Digest {binding.Digest}. License digest {binding.LicenseSha256}. Approved {binding.ApprovedUtc:u}.";
        target.Text = value;
        AutomationProperties.SetName(target, value);
    }

    private void SetDetails(string value)
    {
        ModelDetailsText.Text = value;
        AutomationProperties.SetName(ModelDetailsText, value);
    }

    private void SetStatus(string value)
    {
        StatusText.Text = value;
        AutomationProperties.SetName(StatusText, value);
    }

    private void SetLicense(string value) => ModelLicenseText.Text = value;

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record CandidateRow(
        LocalModelDescriptor Descriptor,
        bool EligibleVision,
        bool EligibleEmbedding)
    {
        public string Display =>
            $"{Descriptor.Name}. Digest {Descriptor.Digest}. " +
            $"{Descriptor.SizeBytes:N0} bytes. " +
            $"Capabilities: {(Descriptor.SupportsVision ? "vision " : string.Empty)}" +
            $"{(Descriptor.SupportsEmbeddings ? "embedding " : string.Empty)}" +
            $"{(EligibleVision || EligibleEmbedding ? "Eligible." : "Not eligible.")}";
    }
}
