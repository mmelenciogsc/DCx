namespace DCx.Core.LocalModels;

/// <summary>
/// Owns the one Ollama process used by an interactive DCx application instance.
/// Every operation re-reads the protected runtime approval. Warm inference retains
/// only one exact digest-bound model, and any failure tears down the entire process.
/// </summary>
public sealed class OllamaApprovedRuntimeHost : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private readonly OllamaRuntimeSelectionStore store;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private OllamaOwnedRuntime? runtime;
    private ApprovedOllamaRuntimeConfiguration? activeConfiguration;
    private WarmModelIdentity? warmModel;
    private int disposeRequested;

    public OllamaApprovedRuntimeHost(OllamaRuntimeSelectionStore store) =>
        this.store = store ?? throw new ArgumentNullException(nameof(store));

    public int? ActiveProcessId => runtime?.ProcessId;
    public Uri? ActiveBaseAddress => runtime?.BaseAddress;

    public async Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default) =>
        await RunInventoryAsync(
            requestTimeout,
            (client, token) => client.ListModelsAsync(token),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<OllamaRunningModel>> ListRunningModelsAsync(
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default) =>
        await RunInventoryAsync(
            requestTimeout,
            (client, token) => client.ListRunningModelsAsync(token),
            cancellationToken).ConfigureAwait(false);

    public async Task<string> GetVersionAsync(
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default) =>
        await RunInventoryAsync(
            requestTimeout,
            (client, token) => client.GetVersionAsync(token),
            cancellationToken).ConfigureAwait(false);

    public async Task<OllamaModelInspection> InspectModelAsync(
        string model,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default) =>
        await RunInventoryAsync(
            requestTimeout,
            (client, token) => client.InspectModelAsync(model, token),
            cancellationToken).ConfigureAwait(false);

    public async Task<OllamaVisionResult> DescribeImagePinnedAsync(
        string model,
        string digest,
        string licenseSha256,
        string imagePath,
        string prompt,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken = default) =>
        await RunWarmInferenceAsync(
            model,
            digest,
            requestTimeout,
            (client, token) => client.DescribeImagePinnedAsync(
                model,
                digest,
                licenseSha256,
                imagePath,
                prompt,
                token),
            cancellationToken).ConfigureAwait(false);

    public async Task<T> RunInventoryAsync<T>(
        TimeSpan requestTimeout,
        Func<OllamaLocalClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        await RunAsync(
            RuntimeUse.Inventory,
            model: null,
            digest: null,
            requestTimeout,
            operation,
            cancellationToken).ConfigureAwait(false);

    public async Task<T> RunWarmInferenceAsync<T>(
        string model,
        string digest,
        TimeSpan requestTimeout,
        Func<OllamaLocalClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ValidateWarmIdentity(model, digest);
        return await RunAsync(
            RuntimeUse.WarmInference,
            model,
            digest.ToLowerInvariant(),
            requestTimeout,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposeRequested, 1) != 0) return;
        await operationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
        }
    }

    private async Task<T> RunAsync<T>(
        RuntimeUse use,
        string? model,
        string? digest,
        TimeSpan requestTimeout,
        Func<OllamaLocalClient, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Volatile.Read(ref disposeRequested) != 0)
            throw new ObjectDisposedException(nameof(OllamaApprovedRuntimeHost));

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref disposeRequested) != 0)
                throw new ObjectDisposedException(nameof(OllamaApprovedRuntimeHost));

            var approved = (await store.GetStateAsync(cancellationToken).ConfigureAwait(false)).Runtime;
            if (approved is null)
            {
                await StopUnsafeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    "No protected, explicitly approved Ollama runtime is selected.");
            }

            if (runtime is not null &&
                (!runtime.IsRunning || activeConfiguration is null ||
                 !activeConfiguration.HasSameIdentity(approved)))
            {
                await StopUnsafeAsync().ConfigureAwait(false);
            }

            if (use == RuntimeUse.WarmInference && warmModel is not null &&
                (!string.Equals(warmModel.Model, model, StringComparison.Ordinal) ||
                 !string.Equals(warmModel.Digest, digest, StringComparison.OrdinalIgnoreCase)))
            {
                await StopUnsafeAsync().ConfigureAwait(false);
            }

            if (runtime is null)
            {
                runtime = await store.StartApprovedAsync(StartupTimeout, cancellationToken)
                    .ConfigureAwait(false);
                var afterStart = (await store.GetStateAsync(cancellationToken).ConfigureAwait(false)).Runtime;
                if (afterStart is null || !approved.HasSameIdentity(afterStart))
                {
                    await StopUnsafeAsync().ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "The protected Ollama runtime approval changed while its process was starting.");
                }
                activeConfiguration = afterStart;
            }

            using var client = use == RuntimeUse.WarmInference
                ? runtime.CreateRetainedModelClient(requestTimeout)
                : runtime.CreateClient(requestTimeout);
            try
            {
                var result = await operation(client, cancellationToken).ConfigureAwait(false);
                if (use == RuntimeUse.WarmInference)
                {
                    warmModel = new WarmModelIdentity(model!, digest!);
                }
                return result;
            }
            catch
            {
                await StopUnsafeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task StopUnsafeAsync()
    {
        var stopping = runtime;
        runtime = null;
        activeConfiguration = null;
        warmModel = null;
        if (stopping is not null)
        {
            await stopping.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void ValidateWarmIdentity(string model, string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 256 || model.Contains('\0') || model.Any(char.IsControl))
            throw new ArgumentException("The Ollama model name is invalid.", nameof(model));
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 ||
            !digest.All(char.IsAsciiHexDigit))
            throw new ArgumentException(
                "An Ollama warm-model binding requires a 64-character SHA-256 digest.",
                nameof(digest));
    }

    private enum RuntimeUse
    {
        Inventory,
        WarmInference,
    }

    private sealed record WarmModelIdentity(string Model, string Digest);
}
