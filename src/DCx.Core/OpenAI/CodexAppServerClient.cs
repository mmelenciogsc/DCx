using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DCx.Core.OpenAI;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private const int MaximumDiagnosticCharacters = 32_768;
    private static readonly string[] SafeInheritedEnvironmentVariables =
    [
        "APPDATA", "COMSPEC", "LOCALAPPDATA", "PATH", "PATHEXT", "PROGRAMDATA",
        "SYSTEMDRIVE", "SYSTEMROOT", "TEMP", "TMP", "USERPROFILE", "WINDIR",
    ];

    private readonly CodexAppServerOptions _options;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, TurnState> _turns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _diagnosticLock = new();
    private readonly StringBuilder _diagnostics = new();
    private readonly SecretRedactor _redactor;
    private Process? _process;
    private StreamWriter? _input;
    private CancellationTokenSource? _lifetime;
    private Task? _readerTask;
    private Task? _errorReaderTask;
    private long _nextRequestId;

    public CodexAppServerClient(CodexAppServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        var secretNames = options.SecretEnvironmentVariableNames ?? [];
        var environmentSecrets = (options.EnvironmentVariables ?? new Dictionary<string, string?>())
            .Where(pair => secretNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
            .Select(pair => pair.Value ?? string.Empty);
        _redactor = new SecretRedactor(environmentSecrets.Concat(options.SensitiveValues ?? []));
    }

    public bool IsReady { get; private set; }

    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    internal int ActiveTurnCount => _turns.Count;

    public string Diagnostics
    {
        get
        {
            lock (_diagnosticLock)
            {
                return _diagnostics.ToString();
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: false })
        {
            throw new InvalidOperationException("The Codex app-server process is already running.");
        }

        ValidateOptions();
        IsReady = false;
        _lifetime = new CancellationTokenSource();
        var startInfo = CreateStartInfo();
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!_process.Start())
        {
            throw new CodexProtocolException("Windows did not start the local Codex app-server process.");
        }

        _input = _process.StandardInput;
        _input.AutoFlush = true;
        _readerTask = ReadMessagesAsync(_process.StandardOutput, _lifetime.Token);
        _errorReaderTask = ReadDiagnosticsAsync(_process.StandardError, _lifetime.Token);

        try
        {
            await SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = "dcx_agentic_browser", title = "DCx / Agentic Browser", version = "0.1.0" },
                },
                cancellationToken);
            await SendNotificationAsync("initialized", new { }, cancellationToken);
            IsReady = true;
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync();
        await StartAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CodexModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        var response = await SendRequestAsync("model/list", new { includeHidden = false }, cancellationToken);
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            throw new CodexProtocolException("Codex returned an invalid model/list response.");
        }

        var models = new List<CodexModel>();
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadRequiredString(item, "model");
            var displayName = item.TryGetProperty("displayName", out var displayNameElement)
                ? displayNameElement.GetString() ?? id
                : id;
            var isDefault = item.TryGetProperty("isDefault", out var defaultElement) && defaultElement.GetBoolean();
            var isHidden = item.TryGetProperty("hidden", out var hiddenElement) && hiddenElement.GetBoolean();
            models.Add(new CodexModel(id, displayName, isDefault, isHidden));
        }

        return models;
    }

    public async Task LoginWithApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        await SendRequestAsync("account/login/start", new { type = "apiKey", apiKey }, cancellationToken);
    }

    public async Task<CodexTurnResult> RunTurnAsync(
        string model,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        if (prompt.Length > 100_000)
        {
            throw new ArgumentException("The prompt exceeds the local client limit.", nameof(prompt));
        }

        var threadResponse = await SendRequestAsync(
            "thread/start",
            new
            {
                model,
                cwd = Path.GetFullPath(_options.WorkingDirectory),
                ephemeral = true,
                sandbox = "read-only",
                approvalPolicy = "never",
            },
            cancellationToken);
        var threadId = ReadNestedRequiredString(threadResponse, "result", "thread", "id");

        cancellationToken.ThrowIfCancellationRequested();
        // Once turn/start is sent, its bounded response supplies the only turn identity that can
        // be interrupted safely. A notification may arrive before that response, so user
        // cancellation must not abandon the identity-bearing request after dispatch.
        var turnResponse = await SendRequestAsync(
            "turn/start",
            new { threadId, input = new[] { new { type = "text", text = prompt } } },
            CancellationToken.None);
        var turnId = ReadNestedRequiredString(turnResponse, "result", "turn", "id");
        var state = _turns.GetOrAdd(turnId, _ => new TurnState(threadId, turnId));

        using var cancellationRegistration = cancellationToken.Register(() =>
            _ = InterruptForCancellationAsync(threadId, turnId, state));
        try
        {
            return await state.Completion.Task.WaitAsync(_lifetime?.Token ?? CancellationToken.None);
        }
        finally
        {
            _turns.TryRemove(turnId, out _);
        }
    }

    public async Task InterruptAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await SendRequestAsync("turn/interrupt", new { threadId, turnId }, cancellationToken);
    }

    public async Task StopAsync()
    {
        IsReady = false;
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            _input?.Close();
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            _lifetime?.Cancel();
            FailOutstanding(new CodexProtocolException("The local Codex app-server stopped."));
            await AwaitQuietly(_readerTask);
            await AwaitQuietly(_errorReaderTask);
            process.Dispose();
            _process = null;
            _input = null;
            _lifetime?.Dispose();
            _lifetime = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeGate.Dispose();
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_options.ExecutablePath),
            WorkingDirectory = Path.GetFullPath(_options.WorkingDirectory),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment.Clear();
        foreach (var variableName in SafeInheritedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                startInfo.Environment[variableName] = value;
            }
        }

        foreach (var pair in _options.EnvironmentVariables ?? new Dictionary<string, string?>())
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        foreach (var argument in _options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        EnsureProcessRunning();
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new CodexProtocolException("Could not allocate a Codex request identifier.");
        }

        try
        {
            await WriteAsync(JsonSerializer.Serialize(new { method, id = requestId, @params = parameters }), cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime!.Token);
            timeout.CancelAfter(_options.EffectiveRequestTimeout);
            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CodexProtocolException($"Codex did not answer {method} within the configured timeout.", exception);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(JsonSerializer.Serialize(new { method, @params = parameters }), cancellationToken);

    private async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            EnsureProcessRunning();
            await _input!.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadMessagesAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                ProcessMessage(line);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                FailOutstanding(new CodexProtocolException("The local Codex app-server closed its output stream."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailOutstanding(new CodexProtocolException("The local Codex app-server protocol stream failed.", exception));
        }
    }

    private void ProcessMessage(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number)
        {
            var id = idElement.GetInt64();
            if (_pending.TryGetValue(id, out var pending))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageElement)
                        ? messageElement.GetString() ?? "Codex request failed."
                        : "Codex request failed.";
                    pending.TrySetException(new CodexProtocolException(_redactor.Redact(message)));
                }
                else
                {
                    pending.TrySetResult(root.Clone());
                }
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodElement) ||
            !root.TryGetProperty("params", out var parameters))
        {
            return;
        }

        switch (methodElement.GetString())
        {
            case "item/agentMessage/delta":
                var deltaTurnId = ReadRequiredString(parameters, "turnId");
                var delta = ReadRequiredString(parameters, "delta");
                _turns.GetOrAdd(deltaTurnId, id => new TurnState(ReadRequiredString(parameters, "threadId"), id))
                    .Append(delta);
                break;
            case "turn/completed":
                var turn = parameters.GetProperty("turn");
                var completedTurnId = ReadRequiredString(turn, "id");
                var status = ReadRequiredString(turn, "status");
                _turns.GetOrAdd(
                        completedTurnId,
                        id => new TurnState(ReadRequiredString(parameters, "threadId"), id))
                    .Complete(status);
                break;
        }
    }

    private async Task ReadDiagnosticsAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                lock (_diagnosticLock)
                {
                    var safeLine = _redactor.Redact(line);
                    var remaining = MaximumDiagnosticCharacters - _diagnostics.Length;
                    if (remaining > 0)
                    {
                        _diagnostics.AppendLine(safeLine[..Math.Min(safeLine.Length, remaining)]);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task InterruptForCancellationAsync(string threadId, string turnId, TurnState state)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.EffectiveRequestTimeout);
            await InterruptAsync(threadId, turnId, timeout.Token);
        }
        catch (Exception exception)
        {
            state.Fail(new CodexProtocolException("Codex turn cancellation failed safely.", exception));
        }
    }

    private void FailOutstanding(Exception exception)
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(exception);
        }

        foreach (var turn in _turns.Values)
        {
            turn.Fail(exception);
        }
    }

    private void ValidateOptions()
    {
        if (!Path.IsPathFullyQualified(_options.ExecutablePath) || !File.Exists(_options.ExecutablePath))
        {
            throw new FileNotFoundException("A fully qualified local Codex app-server executable is required.");
        }

        if (!Path.IsPathFullyQualified(_options.WorkingDirectory) || !Directory.Exists(_options.WorkingDirectory))
        {
            throw new DirectoryNotFoundException("A fully qualified existing Codex working directory is required.");
        }

        if (_options.EffectiveRequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(_options), "The request timeout must be positive.");
        }
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException("The local Codex app-server handshake is not complete.");
        }
    }

    private void EnsureProcessRunning()
    {
        if (_process is null || _process.HasExited || _input is null || _lifetime is null)
        {
            throw new InvalidOperationException("The local Codex app-server is not running.");
        }
    }

    private static string ReadRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new CodexProtocolException($"Codex response omitted required property {propertyName}.");
        }

        return property.GetString()!;
    }

    private static string ReadNestedRequiredString(JsonElement element, params string[] propertyPath)
    {
        var current = element;
        foreach (var property in propertyPath)
        {
            if (!current.TryGetProperty(property, out current))
            {
                throw new CodexProtocolException($"Codex response omitted required property {string.Join('.', propertyPath)}.");
            }
        }

        return current.GetString() ?? throw new CodexProtocolException(
            $"Codex property {string.Join('.', propertyPath)} was not a string.");
    }

    private static async Task AwaitQuietly(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class TurnState(string threadId, string turnId)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public TaskCompletionSource<CodexTurnResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Append(string delta)
        {
            lock (_gate)
            {
                _text.Append(delta);
            }
        }

        public void Complete(string status)
        {
            string text;
            lock (_gate)
            {
                text = _text.ToString();
            }

            Completion.TrySetResult(new CodexTurnResult(threadId, turnId, status, text));
        }

        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }
}
