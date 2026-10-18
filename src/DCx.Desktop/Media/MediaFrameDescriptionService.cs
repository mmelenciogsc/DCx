using DCx.Core.Content;
using DCx.Core.LocalModels;

namespace DCx.Desktop.Media;

public sealed class MediaFrameDescriptionService
{
    private readonly OllamaApprovedRuntimeHost runtimeHost;

    public MediaFrameDescriptionService(OllamaApprovedRuntimeHost runtimeHost) =>
        this.runtimeHost = runtimeHost ?? throw new ArgumentNullException(nameof(runtimeHost));

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
        return await runtimeHost.DescribeImagePinnedAsync(
            model,
            modelDigest,
            modelLicenseSha256,
            frame.Path,
            prompt.Prompt,
            TimeSpan.FromMinutes(5),
            cancellationToken).ConfigureAwait(false);
    }
}
