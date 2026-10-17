using DCx.Core.Content;
using DCx.Core.LocalModels;

namespace DCx.Desktop.Media;

public sealed class MediaFrameDescriptionService
{
    public async Task<OllamaVisionResult> DescribeAsync(
        string mediaPath,
        TimeSpan position,
        ApprovedFfmpegBinding ffmpegBinding,
        string model,
        string modelDigest,
        string modelLicenseSha256,
        string? question,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ffmpegBinding);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var prompt = new MediaFrameQuestionPolicy().Evaluate(question);
        if (!prompt.IsAllowed) throw new ArgumentException(prompt.Reason, nameof(question));
        var processor = ffmpegBinding.CreateProcessor(TimeSpan.FromSeconds(20));
        await using var frame = await processor.ExtractFrameAsync(mediaPath, position, cancellationToken);
        using var ollama = new OllamaLocalClient(
            new Uri("http://localhost:11434/"),
            TimeSpan.FromMinutes(3));
        return await ollama.DescribeImagePinnedAsync(
            model,
            modelDigest,
            modelLicenseSha256,
            frame.Path,
            prompt.Prompt,
            cancellationToken);
    }
}
