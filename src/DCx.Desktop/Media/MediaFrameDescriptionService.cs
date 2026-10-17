using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using DCx.Core.LocalModels;

namespace DCx.Desktop.Media;

public sealed class MediaFrameDescriptionService
{
    public async Task<OllamaVisionResult> DescribeAsync(
        string mediaPath,
        TimeSpan position,
        string model,
        string modelDigest,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = FindExecutable("ffmpeg.exe")
            ?? throw new InvalidOperationException("FFmpeg is unavailable on the Windows PATH.");
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var tempRoot = Path.Combine(Path.GetTempPath(), "DCx", "frames");
        Directory.CreateDirectory(tempRoot);
        var framePath = Path.Combine(tempRoot, $"frame-{Guid.NewGuid():N}.png");
        try
        {
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
            if (process.ExitCode != 0 || !File.Exists(framePath))
                throw new InvalidOperationException($"Frame extraction failed. {await errorTask}");

            using var ollama = new OllamaLocalClient(
                new Uri("http://localhost:11434/"),
                TimeSpan.FromMinutes(3));
            return await ollama.DescribeImagePinnedAsync(
                model,
                modelDigest,
                framePath,
                "Directly describe this paused video frame in one concise sentence. Mention meaningful subjects, action, setting, and text. Do not begin with 'the image' or 'the scene'.",
                cancellationToken);
        }
        finally
        {
            if (File.Exists(framePath)) File.Delete(framePath);
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
