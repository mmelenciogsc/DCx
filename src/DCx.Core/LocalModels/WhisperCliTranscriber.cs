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
    long MaximumTranscriptBytes = 1024L * 1024,
    long MaximumExecutableBytes = 1024L * 1024 * 1024,
    long MaximumModelBytes = 16L * 1024 * 1024 * 1024);

public sealed record WhisperTranscriptionResult(
    string Text,
    TimeSpan Elapsed,
    long? PeakWorkingSetBytes);

public sealed class WhisperCliTranscriber
{
    private readonly WhisperCliConfiguration configuration;
    private readonly string executablePath;
    private readonly string modelPath;

    public WhisperCliTranscriber(WhisperCliConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateDigest(configuration.ExecutableSha256, nameof(configuration.ExecutableSha256));
        ValidateDigest(configuration.ModelSha256, nameof(configuration.ModelSha256));
        if (configuration.Timeout < TimeSpan.FromMilliseconds(100) || configuration.Timeout > TimeSpan.FromMinutes(30) ||
            configuration.MaximumAudioBytes is <= 0 or > 2L * 1024 * 1024 * 1024 ||
            configuration.MaximumTranscriptBytes is <= 0 or > 16L * 1024 * 1024 ||
            configuration.MaximumExecutableBytes is <= 0 or > 2L * 1024 * 1024 * 1024 ||
            configuration.MaximumModelBytes is <= 0 or > 32L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "Whisper resource limits are invalid.");
        }

        executablePath = ValidateFile(
            configuration.ExecutablePath,
            ".exe",
            "Whisper executable",
            configuration.MaximumExecutableBytes);
        modelPath = ValidateModel(configuration.ModelPath, configuration.MaximumModelBytes);
        this.configuration = configuration;
    }

    public async Task<WhisperTranscriptionResult> TranscribeWavAsync(
        string audioPath,
        string language = "en",
        CancellationToken cancellationToken = default)
    {
        await using var audioLease = OpenValidatedWave(audioPath, configuration.MaximumAudioBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (language.Length is < 2 or > 16 || !language.All(character => char.IsAsciiLetter(character) || character == '-'))
        {
            throw new ArgumentException("The Whisper language must be a short ASCII language tag.", nameof(language));
        }

        await using var executableLease = await OpenVerifiedPinnedFileAsync(
            executablePath,
            configuration.ExecutableSha256,
            configuration.MaximumExecutableBytes,
            cancellationToken).ConfigureAwait(false);
        await using var modelLease = await OpenVerifiedPinnedFileAsync(
            modelPath,
            configuration.ModelSha256,
            configuration.MaximumModelBytes,
            cancellationToken).ConfigureAwait(false);

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
            AddArgument(startInfo, "-f", audioLease.Name);
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

    private static FileStream OpenValidatedWave(string path, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        RejectNetworkPath(canonical, "Whisper input");
        var file = new FileInfo(canonical);
        if (!file.Exists || !string.Equals(file.Extension, ".wav", StringComparison.OrdinalIgnoreCase) ||
            file.Length is < 12 || file.Length > maximumBytes || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException("Whisper input must be a bounded regular WAV file.", nameof(path));
        }
        RejectReparseChain(canonical, "Whisper input");

        var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length is < 12 || stream.Length > maximumBytes)
            {
                throw new ArgumentException("Whisper input must be a bounded regular WAV file.", nameof(path));
            }
            Span<byte> header = stackalloc byte[12];
            if (stream.Read(header) != header.Length || !header.StartsWith("RIFF"u8) || !header[8..].StartsWith("WAVE"u8))
            {
                throw new ArgumentException("Whisper input does not have a WAV signature.", nameof(path));
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string ValidateModel(string path, long maximumBytes)
    {
        var canonical = ValidateFile(path, null, "Whisper model", maximumBytes);
        var extension = Path.GetExtension(canonical);
        if (!string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".gguf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Whisper model must be a .bin or .gguf file.", nameof(path));
        }

        return canonical;
    }

    private static string ValidateFile(string path, string? extension, string label, long maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        RejectNetworkPath(canonical, label);
        var file = new FileInfo(canonical);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            extension is not null && !string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{label} must be an existing regular {extension ?? "file"}.", nameof(path));
        }
        RejectReparseChain(canonical, label);

        return canonical;
    }

    private static void ValidateDigest(string digest, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(digest) || digest.Length != 64 || !digest.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A pinned SHA-256 digest must contain 64 hexadecimal characters.", parameterName);
        }
    }

    private static async Task<FileStream> OpenVerifiedPinnedFileAsync(
        string path,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FileStream? lease = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
                (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("A pinned local Whisper executable or model is unavailable or unsafe.");
            }
            RejectReparseChain(path, "Pinned local Whisper file");

            lease = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (lease.Length <= 0 || lease.Length > maximumBytes)
            {
                throw new InvalidOperationException("A pinned local Whisper executable or model is outside its size limit.");
            }
            var actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A pinned local Whisper executable or model failed SHA-256 verification.");
            }

            lease.Position = 0;
            var result = lease;
            lease = null;
            return result;
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void RejectNetworkPath(string path, string label)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{label} must be on a local drive.", nameof(path));
        }
    }

    private static void RejectReparseChain(string path, string label)
    {
        var file = new FileInfo(path);
        DirectoryInfo? parent = file.Directory;
        while (parent is not null)
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException($"{label} cannot cross a reparse directory.");
            }
            parent = parent.Parent;
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

    private static async Task<long?> MonitorWorkingSetAsync(Process process)
    {
        long? maximum = null;
        while (true)
        {
            try
            {
                process.Refresh();
                var sample = Math.Max(process.WorkingSet64, process.PeakWorkingSet64);
                if (sample > 0) maximum = Math.Max(maximum ?? 0, sample);
                if (process.HasExited) return maximum;
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
