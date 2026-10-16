using System.IO;
using System.Windows;
using DCx.Core.OpenAI;
using DCx.Core.Security;
using DCx.Desktop.OpenAI;

namespace DCx.Desktop;

public partial class OpenAiControlWindow : Window
{
    private const string ProductionApiKeyCredentialTarget = "DCx/OpenAI/APIKey";
    private const bool CommercialChatGptApprovalVerified = false;
    private readonly string _repositoryRoot;
    private readonly WindowsCredentialVault _credentialVault = new();
    private readonly string _apiKeyCredentialTarget;
    private readonly CodexRuntimeDescriptor? _runtime;
    private CodexAppServerClient? _client;
    private CancellationTokenSource? _turnCancellation;

    public OpenAiControlWindow(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _apiKeyCredentialTarget = ResolveCredentialTarget();
        _runtime = CodexRuntimeLocator.Find(_repositoryRoot);
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshAvailability();

    private async void OnClosed(object? sender, EventArgs e)
    {
        _turnCancellation?.Cancel();
        _turnCancellation?.Dispose();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    private void OnSaveApiKeyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiKeyPassword.Password))
        {
            SetStatus("Enter an API key before saving.");
            ApiKeyPassword.Focus();
            return;
        }

        _credentialVault.Save(_apiKeyCredentialTarget, ApiKeyPassword.Password);
        ApiKeyPassword.Clear();
        SetStatus("API key saved in Windows Credential Manager. It was not tested or logged.");
        RefreshAvailability();
    }

    private void OnDeleteApiKeyClick(object sender, RoutedEventArgs e)
    {
        _credentialVault.Delete(_apiKeyCredentialTarget);
        ApiKeyPassword.Clear();
        SetStatus("Saved API key deleted from Windows Credential Manager.");
        RefreshAvailability();
    }

    private async void OnConnectCodexClick(object sender, RoutedEventArgs e)
    {
        if (_runtime is null)
        {
            SetStatus("No supported Windows codex.exe installation was found.");
            return;
        }

        string? apiKey = null;
        if (!_runtime.IsTestFixture)
        {
            apiKey = _credentialVault.Read(_apiKeyCredentialTarget);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                SetStatus("Save an OpenAI API key before connecting the production Codex runtime.");
                return;
            }
        }

        ConnectCodexButton.IsEnabled = false;
        SetStatus("Starting the per-user local Codex app-server.");
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync();
            }

            var environment = new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = GetCodexHome(),
            };
            _client = new CodexAppServerClient(new CodexAppServerOptions(
                _runtime.ExecutablePath,
                _runtime.Arguments,
                _repositoryRoot,
                environment,
                SensitiveValues: apiKey is null ? [] : [apiKey]));
            await _client.StartAsync();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                await _client.LoginWithApiKeyAsync(apiKey);
            }

            var models = await _client.ListModelsAsync();
            ModelsBox.ItemsSource = models.Where(model => !model.IsHidden).ToArray();
            ModelsBox.SelectedItem = models.FirstOrDefault(model => model.IsDefault) ?? models.FirstOrDefault();
            DisconnectCodexButton.IsEnabled = true;
            RunTurnButton.IsEnabled = ModelsBox.SelectedItem is not null;
            SetStatus($"Local Codex connection ready. {models.Count} model{(models.Count == 1 ? string.Empty : "s")} reported by this runtime.");
        }
        catch (Exception exception)
        {
            SetStatus($"Local Codex connection failed safely. {new SecretRedactor(apiKey is null ? [] : [apiKey]).Redact(exception.Message)}");
            ConnectCodexButton.IsEnabled = true;
            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }
        }
    }

    private async void OnDisconnectCodexClick(object sender, RoutedEventArgs e)
    {
        _turnCancellation?.Cancel();
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        ModelsBox.ItemsSource = null;
        RunTurnButton.IsEnabled = false;
        CancelTurnButton.IsEnabled = false;
        DisconnectCodexButton.IsEnabled = false;
        ConnectCodexButton.IsEnabled = _runtime is not null;
        SetStatus("Local Codex app-server disconnected.");
    }

    private async void OnRunTurnClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || ModelsBox.SelectedItem is not CodexModel model)
        {
            SetStatus("Connect and choose an available model first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PromptBox.Text))
        {
            SetStatus("Enter a test prompt first.");
            PromptBox.Focus();
            return;
        }

        _turnCancellation?.Dispose();
        _turnCancellation = new CancellationTokenSource();
        RunTurnButton.IsEnabled = false;
        CancelTurnButton.IsEnabled = true;
        CodexResponseText.Clear();
        SetStatus("Codex turn running. Activate Cancel turn to interrupt it.");
        try
        {
            var result = await _client.RunTurnAsync(model.Id, PromptBox.Text, _turnCancellation.Token);
            CodexResponseText.Text = result.Text;
            SetStatus(result.Status == "completed"
                ? "Codex turn completed."
                : $"Codex turn ended with status {result.Status}.");
            CodexResponseText.Focus();
        }
        catch (Exception exception)
        {
            SetStatus($"Codex turn failed safely. {exception.Message}");
        }
        finally
        {
            RunTurnButton.IsEnabled = _client.IsReady;
            CancelTurnButton.IsEnabled = false;
        }
    }

    private void OnCancelTurnClick(object sender, RoutedEventArgs e)
    {
        _turnCancellation?.Cancel();
        SetStatus("Cancellation requested. Waiting for the local Codex app-server to confirm interruption.");
    }

    private void RefreshAvailability()
    {
        var hasKey = _credentialVault.Read(_apiKeyCredentialTarget) is not null;
        var access = OpenAiAccessPolicy.Evaluate(hasKey, CommercialChatGptApprovalVerified);
        ProviderAvailabilityText.Text = access.ApiKeyModeAvailable
            ? "API-key mode is configured. No request runs until you activate Run turn."
            : "API-key mode is not configured. No OpenAI request can run.";
        ChatGptEligibilityText.Text = access.ChatGptPlanReason;
        ChatGptSignInButton.IsEnabled = access.ChatGptPlanModeAvailable;
        DeleteApiKeyButton.IsEnabled = hasKey;
        CodexRuntimeText.Text = _runtime is null
            ? "Local Codex runtime unavailable. Install an approved Windows codex.exe or configure its full path."
            : _runtime.IsTestFixture
                ? "Offline Codex protocol fixture detected for validation."
                : $"Local Codex runtime detected: {Path.GetFileName(_runtime.ExecutablePath)}.";
        ConnectCodexButton.IsEnabled = _runtime is not null && (_runtime.IsTestFixture || hasKey);
    }

    private static string GetCodexHome()
    {
        var dataRoot = Environment.GetEnvironmentVariable("DCX_DATA_ROOT");
        var root = string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DCx")
            : Path.GetFullPath(dataRoot);
        var codexHome = Path.Combine(root, "Codex");
        Directory.CreateDirectory(codexHome);
        return codexHome;
    }

    private static string ResolveCredentialTarget()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DCX_TEST_MODE"), "1", StringComparison.Ordinal))
        {
            var testTarget = Environment.GetEnvironmentVariable("DCX_TEST_CREDENTIAL_TARGET");
            if (!string.IsNullOrWhiteSpace(testTarget) && testTarget.StartsWith("DCx/Test/", StringComparison.Ordinal))
            {
                return testTarget;
            }
        }

        return ProductionApiKeyCredentialTarget;
    }

    private void SetStatus(string message) => CodexStatusText.Text = message;
}
