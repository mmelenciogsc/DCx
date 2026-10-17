using System.Diagnostics;
using System.Security.Cryptography;

namespace DCx.Core.LocalModels;

public sealed record WhisperCliConfiguration(
    string ExecutablePath,
    string ExecutableSha256,
    string ModelPath,
    string ModelSha256,
    TimeSpan Timeout,
    long MaximumAudioBytes = 512L * 1024 * 1024,
    long MaximumTranscriptBytes = 1024L * 1024);

public sealed record WhisperTranscriptionResult(
    string Text,
    TimeSpan Elapsed,
    long PeakWorkingSetBytes);

public sealed class WhisperCliTranscriber
{
    private readonly WhisperCliConfiguration configuration;
    private readonly string executablePath;
    private readonly string modelPath;

    public WhisperCliTranscriber(WhisperCliConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        executablePath = ValidateFile(configuration.ExecutablePath, ".exe", "Whisper executable");
        modelPath = ValidateModel(configuration.ModelPath);
        ValidateDigest(configuration.ExecutableSha256, nameof(configuration.ExecutableSha256));
        ValidateDigest(configuration.ModelSha256, nameof(configuration.ModelSha256));
        if (configuration.Timeout < TimeSpan.FromMilliseconds(100) || configuration.Timeout > TimeSpan.FromMinutes(30) ||
            configuration.MaximumAudioBytes is <= 0 or > 2L * 1024 * 1024 * 1024 ||
            configuration.MaximumTranscriptBytes is <= 0 or > 16L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "Whisper resource limits are invalid.");
        }

        this.configuration = configuration;
    }

    public async Task<WhisperTranscriptionResult> TranscribeWavAsync(
        string audioPath,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        var audio = ValidateWave(audioPath, configuration.MaximumAudioBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (language.Length is < 2 or > 16 || !language.All(character => char.IsAsciiLetter(character) || character == '-'))
        {
            throw new ArgumentException("The Whisper language must be a short ASCII language tag.", nameof(language));
        }

        await VerifyPinnedFileAsync(executablePath, configuration.ExecutableSha256, cancellationToken).ConfigureAwait(false);
        await VerifyPinnedFileAsync(modelPath, configuration.ModelSha256, cancellationToken).ConfigureAwait(false);

        var operationRoot = Path.Combine(Path.GetTempPath(), "DCx", "whisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operationRoot);
        var outputPrefix = Path.Combine(operationRoot, "transcript");
        var outputPath = $"{outputPrefix}.txt";
        var started = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = operationRoot,
            };
            startInfo.Environment.Clear();
            CopyEnvironment(startInfo, "SystemRoot");
            CopyEnvironment(startInfo, "WINDIR");
            startInfo.Environment["TEMP"] = operationRoot;
            startInfo.Environment["TMP"] = operationRoot;
            AddArgument(startInfo, "-m", modelPath);
            AddArgument(startInfo, "-f", audio.FullName);
            AddArgument(startInfo, "-l", language);
            AddArgument(startInfo, "-of", outputPrefix);
            startInfo.ArgumentList.Add("-otxt");
            startInfo.ArgumentList.Add("-nt");
            startInfo.ArgumentList.Add("-np");

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The local Whisper process could not start.");
            var workingSet = MonitorWorkingSetAsync(process);
            var stdout = DrainBoundedAsync(process.StandardOutput);
            var stderr = DrainBoundedAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await workingSet.ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw new TimeoutException("The local Whisper process exceeded its approved time limit.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await workingSet.ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var peakWorkingSetBytes = await workingSet.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"The local Whisper process failed with exit code {process.ExitCode}.");
            }

            var output = new FileInfo(outputPath);
            if (!output.Exists || output.Length is <= 0 || output.Length > configuration.MaximumTranscriptBytes ||
                (output.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The local Whisper transcript is missing or outside its size limit.");
            }

            var text = (await File.ReadAllTextAsync(output.FullName, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The local Whisper transcript is empty.");
            return new WhisperTranscriptionResult(text, started.Elapsed, peakWorkingSetBytes);
        }
        finally
        {
            if (process is { HasExited: false }) Kill(process);
            process?.Dispose();
            if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, recursive: true);
        }
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        var canonical = Path.GetFullPath(path);
        await using var stream = new FileStream(
            canonical,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static FileInfo ValidateWave(string path, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || !string.Equals(file.Extension, ".wav", StringComparison.OrdinalIgnoreCase) ||
            file.Length is < 12 || file.Length > maximumBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Whisper input must be a bounded regular WAV file.", nameof(path));
        }

        Span<byte> header = stackalloc byte[12];
        using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Read(header) != header.Length || !header.StartsWith("RIFF"u8) || !header[8..].StartsWith("WAVE"u8))
        {
            throw new ArgumentException("Whisper input does not have a WAV signature.", nameof(path));
        }

        return file;
    }

    private static string ValidateModel(string path)
    {
        var canonical = ValidateFile(path, null, "Whisper model");
        var extension = Path.GetExtension(canonical);
        if (!string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Whisper model must be a .bin or .gguf file.", nameof(path));
        }

        return canonical;
    }

    private static string ValidateFile(string path, string? extension, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        var file = new FileInfo(canonical);
        if (!file.Exists || file.Length <= 0 || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            extension is not null && !string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{label} must be an existing regular {extension ?? "file"}.", nameof(path));
        }

        return canonical;
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A pinned SHA-256 digest must contain 64 hexadecimal characters.", parameterName);
        }
    }

    private static async Task VerifyPinnedFileAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A pinned local Whisper executable or model failed SHA-256 verification.");
        }
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static void CopyEnvironment(ProcessStartInfo startInfo, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) startInfo.Environment[name] = value;
    }

    private static async Task DrainBoundedAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
        {
            // Drain to prevent child-process pipe blocking. Diagnostics are intentionally not retained.
        }
    }

    private static async Task<long> MonitorWorkingSetAsync(Process process)
    {
        long maximum = 0;
        while (true)
        {
            try
            {
                if (process.HasExited) return maximum;
                process.Refresh();
                maximum = Math.Max(maximum, process.WorkingSet64);
            }
            catch (InvalidOperationException)
            {
                return maximum;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
