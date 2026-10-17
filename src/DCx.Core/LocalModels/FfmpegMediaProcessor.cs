using System.Diagnostics;
using System.Security.Cryptography;

namespace DCx.Core.LocalModels;

public sealed record FfmpegMediaProcessorConfiguration(
    string ExecutablePath,
    string ExecutableSha256,
    TimeSpan Timeout,
    long MaximumInputBytes = 4L * 1024 * 1024 * 1024,
    long MaximumWaveBytes = 512L * 1024 * 1024,
    long MaximumFrameBytes = 20L * 1024 * 1024,
    long MaximumExecutableBytes = FfmpegBackendReviewer.MaximumExecutableBytes);

public sealed class FfmpegOutputArtifact : IAsyncDisposable
{
    private FileStream? lease;
    private readonly string operationRoot;

    internal FfmpegOutputArtifact(string path, string operationRoot, FileStream lease)
    {
        Path = path;
        this.operationRoot = operationRoot;
        this.lease = lease;
    }

    public string Path { get; }
    public long Length => lease?.Length ?? throw new ObjectDisposedException(nameof(FfmpegOutputArtifact));

    public async ValueTask DisposeAsync()
    {
        if (lease is null) return;
        await lease.DisposeAsync().ConfigureAwait(false);
        lease = null;
        try { if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}

public sealed class FfmpegMediaProcessor
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly FfmpegMediaProcessorConfiguration configuration;
    private readonly string executablePath;

    public FfmpegMediaProcessor(FfmpegMediaProcessorConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Timeout < TimeSpan.FromMilliseconds(100) || configuration.Timeout > TimeSpan.FromMinutes(30) ||
            configuration.MaximumInputBytes is <= 0 or > 16L * 1024 * 1024 * 1024 ||
            configuration.MaximumWaveBytes is <= 12 or > 2L * 1024 * 1024 * 1024 ||
            configuration.MaximumFrameBytes is <= 8 or > 128L * 1024 * 1024 ||
            configuration.MaximumExecutableBytes is <= 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(configuration), "FFmpeg resource limits are invalid.");
        ValidateDigest(configuration.ExecutableSha256);
        executablePath = ValidateRegularLocalFile(
            configuration.ExecutablePath, ".exe", configuration.MaximumExecutableBytes, "FFmpeg executable");
        this.configuration = configuration;
    }

    public Task<FfmpegOutputArtifact> ExtractWaveAsync(string mediaPath, CancellationToken cancellationToken = default) =>
        ExecuteAsync(mediaPath, isFrame: false, TimeSpan.Zero, cancellationToken);

    public Task<FfmpegOutputArtifact> ExtractFrameAsync(
        string mediaPath,
        TimeSpan position,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(position.TotalSeconds) || position < TimeSpan.Zero || position > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(position));
        return ExecuteAsync(mediaPath, isFrame: true, position, cancellationToken);
    }

    private async Task<FfmpegOutputArtifact> ExecuteAsync(
        string mediaPath,
        bool isFrame,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        var inputPath = ValidateInputPath(mediaPath, configuration.MaximumInputBytes, isFrame);
        await using var inputLease = OpenReadLease(inputPath, configuration.MaximumInputBytes, "Media input");
        ValidateInputSignature(inputLease, Path.GetExtension(inputPath));
        await using var executableLease = await OpenVerifiedExecutableAsync(cancellationToken).ConfigureAwait(false);
        var operationRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DCx", "ffmpeg", Guid.NewGuid().ToString("N"));
        var outputPath = System.IO.Path.Combine(operationRoot, isFrame ? "frame.png" : "audio.wav");
        Process? process = null;
        try
        {
            Directory.CreateDirectory(operationRoot);
            if ((File.GetAttributes(operationRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Private FFmpeg storage is unsafe.");
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
            Add(startInfo, "-hide_banner", "-nostdin", "-loglevel", "error");
            if (isFrame)
                Add(startInfo, "-ss", Math.Max(0, position.TotalSeconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            Add(startInfo, "-i", inputPath);
            if (isFrame)
                Add(startInfo, "-frames:v", "1", "-f", "image2", "-vcodec", "png", "-y", outputPath);
            else
                Add(startInfo, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-f", "wav", "-y", outputPath);
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The approved FFmpeg process could not start.");
            var stdout = DrainAsync(process.StandardOutput);
            var stderr = DrainAsync(process.StandardError);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(configuration.Timeout);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw new TimeoutException("The approved FFmpeg process exceeded its time limit.");
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("The approved FFmpeg process failed.");
            FileStream? outputLease = OpenReadLease(
                outputPath,
                isFrame ? configuration.MaximumFrameBytes : configuration.MaximumWaveBytes,
                isFrame ? "FFmpeg frame" : "FFmpeg wave");
            try
            {
                var signature = new byte[12];
                var needed = isFrame ? 8 : 12;
                if (outputLease.Read(signature, 0, needed) != needed ||
                    isFrame && !signature.AsSpan(0, 8).SequenceEqual(PngSignature) ||
                    !isFrame && (!signature.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !signature.AsSpan(8, 4).SequenceEqual("WAVE"u8)))
                    throw new InvalidOperationException("The approved FFmpeg output signature is invalid.");
                outputLease.Position = 0;
                var artifact = new FfmpegOutputArtifact(outputPath, operationRoot, outputLease);
                outputLease = null;
                return artifact;
            }
            finally { outputLease?.Dispose(); }
        }
        catch
        {
            try { if (Directory.Exists(operationRoot)) Directory.Delete(operationRoot, true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            if (process is { HasExited: false }) Kill(process);
            process?.Dispose();
        }
    }

    private async Task<FileStream> OpenVerifiedExecutableAsync(CancellationToken cancellationToken)
    {
        _ = ValidateRegularLocalFile(
            executablePath, ".exe", configuration.MaximumExecutableBytes, "FFmpeg executable");
        var lease = OpenReadLease(executablePath, configuration.MaximumExecutableBytes, "FFmpeg executable");
        try
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(lease, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actual, configuration.ExecutableSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("The approved FFmpeg executable failed SHA-256 verification.");
            lease.Position = 0;
            return lease;
        }
        catch { await lease.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static string ValidateInputPath(string path, long maximumBytes, bool frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = ValidateRegularLocalFile(path, null, maximumBytes, "Media input");
        var extension = System.IO.Path.GetExtension(canonical);
        if (frame && !string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) ||
            !frame && !string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("FFmpeg input type is unsupported.", nameof(path));
        return canonical;
    }

    private static void ValidateInputSignature(FileStream stream, string extension)
    {
        var header = new byte[12];
        var read = stream.Read(header, 0, header.Length);
        var valid = string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            ? read >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8)
            : (read >= 3 && header.AsSpan(0, 3).SequenceEqual("ID3"u8)) ||
              (read >= 2 && header[0] == 0xff && (header[1] & 0xe0) == 0xe0);
        stream.Position = 0;
        if (!valid) throw new ArgumentException("FFmpeg input signature is invalid.", "mediaPath");
    }

    private static string ValidateRegularLocalFile(string path, string? extension, long maximumBytes, string label)
    {
        var canonical = System.IO.Path.GetFullPath(path);
        if (canonical.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException($"{label} must be local.", nameof(path));
        var file = new FileInfo(canonical);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            extension is not null && !string.Equals(file.Extension, extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{label} is invalid.", nameof(path));
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException($"{label} crosses a reparse directory.", nameof(path));
        return canonical;
    }

    private static FileStream OpenReadLease(string path, long maximumBytes, string label)
    {
        _ = ValidateRegularLocalFile(path, null, maximumBytes, label);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            stream.Dispose();
            throw new InvalidOperationException($"{label} is outside its size limit.");
        }
        return stream;
    }

    private static void ValidateDigest(string digest)
    {
        if (digest is not { Length: 64 } || !digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new ArgumentException("The FFmpeg SHA-256 digest is invalid.", nameof(digest));
    }

    private static void Add(ProcessStartInfo info, params string[] arguments)
    {
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
    }

    private static void CopyEnvironment(ProcessStartInfo info, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) info.Environment[name] = value;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false) != 0) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
    }
}
