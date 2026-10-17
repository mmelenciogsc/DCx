using System.Diagnostics;
using System.Globalization;
using System.IO;
using DCx.Core.Content;
using DCx.Core.LocalModels;

namespace DCx.Desktop.Media;

public sealed class MediaFrameDescriptionService
{
    public async Task<OllamaVisionResult> DescribeAsync(
        string mediaPath,
        TimeSpan position,
        string model,
        string modelDigest,
        string modelLicenseSha256,
        string? question,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FindExecutable("ffmpeg.exe")
            ?? throw new InvalidOperationException("FFmpeg is unavailable on the Windows PATH.");
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"DCx-local-video-frame-{Guid.NewGuid():N}");
        var framePath = Path.Combine(tempRoot, "paused-frame.png");
        try
        {
            Directory.CreateDirectory(tempRoot);
            if ((File.GetAttributes(tempRoot) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Private frame storage is unsafe.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-ss",
                         Math.Max(0, position.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture),
                         "-i", Path.GetFullPath(mediaPath), "-frames:v", "1", "-y", framePath,
                     })
                startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw;
            }
            _ = await errorTask;
            if (process.ExitCode != 0 || !File.Exists(framePath))
                throw new InvalidOperationException("Frame extraction failed without retaining process diagnostics.");

            var prompt = new MediaFrameQuestionPolicy().Evaluate(question);
            if (!prompt.IsAllowed) throw new ArgumentException(prompt.Reason, nameof(question));

            using var ollama = new OllamaLocalClient(
                new Uri("http://localhost:11434/"),
                TimeSpan.FromMinutes(3));
            await using var readLease = new FileStream(
                framePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ollama.DescribeImagePinnedAsync(
                model,
                modelDigest,
                modelLicenseSha256,
                framePath,
                prompt.Prompt,
                cancellationToken);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The private random frame is best-effort cleanup; no path or process output is surfaced.
            }
        }
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }
}
