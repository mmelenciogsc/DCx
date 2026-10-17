using System.Security.Cryptography;
using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class FfmpegMediaProcessorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-ffmpeg-processor-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExtractWave_ReturnsLeasedValidatedArtifactAndDeletesItOnDispose()
    {
        Directory.CreateDirectory(root);
        var input = await WriteMp3Async("fixture.mp3");
        var processor = await CreateAsync(TimeSpan.FromSeconds(5));

        var artifact = await processor.ExtractWaveAsync(input);
        var output = artifact.Path;
        var operationRoot = Directory.GetParent(output)!.FullName;

        Assert.True(artifact.Length >= 12);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString((await File.ReadAllBytesAsync(output))[..4]));
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(output, [1, 2, 3]));

        await artifact.DisposeAsync();

        Assert.False(File.Exists(output));
        Assert.False(Directory.Exists(operationRoot));
    }

    [Fact]
    public async Task ExtractFrame_AcceptsMp4AndReturnsValidatedPng()
    {
        Directory.CreateDirectory(root);
        var input = await WriteMp4Async("fixture.mp4");
        var processor = await CreateAsync(TimeSpan.FromSeconds(5));

        await using var artifact = await processor.ExtractFrameAsync(input, TimeSpan.FromSeconds(1.25));
        var bytes = await File.ReadAllBytesAsync(artifact.Path);

        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a }, bytes[..8]);
    }

    [Fact]
    public async Task InvalidInputAndOutputSignaturesFailClosedAndLeaveNoArtifact()
    {
        Directory.CreateDirectory(root);
        var invalidInput = Path.Combine(root, "invalid.mp3");
        await File.WriteAllTextAsync(invalidInput, "not mp3");
        var invalidOutput = await WriteMp4Async("invalid-output.mp4");
        var processor = await CreateAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ArgumentException>(() => processor.ExtractWaveAsync(invalidInput));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ExtractFrameAsync(invalidOutput, TimeSpan.Zero));
    }

    [Fact]
    public async Task ExecutableTamperingFailsBeforeProcessExecution()
    {
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "tampered.exe");
        File.Copy(FixtureExecutable(), executable);
        var expectedDigest = await DigestAsync(executable);
        await File.AppendAllTextAsync(executable, "tamper");
        var input = await WriteMp3Async("fixture.mp3");
        var processor = new FfmpegMediaProcessor(new(executable, expectedDigest, TimeSpan.FromSeconds(5)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ExtractWaveAsync(input));
    }

    [Fact]
    public async Task TimeoutAndCancellationKillProcessAndReleaseInputLease()
    {
        Directory.CreateDirectory(root);
        var timeoutInput = await WriteMp3Async("timeout.mp3");
        var processor = await CreateAsync(TimeSpan.FromMilliseconds(250));
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(() => processor.ExtractWaveAsync(timeoutInput));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        await File.WriteAllBytesAsync(timeoutInput, Mp3Bytes());

        var cancellationInput = await WriteMp3Async("timeout-cancel.mp3");
        var cancellable = await CreateAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellable.ExtractWaveAsync(cancellationInput, cancellation.Token));
        await File.WriteAllBytesAsync(cancellationInput, Mp3Bytes());
    }

    [Fact]
    public async Task InputRemainsReadLockedThroughoutChildExecution()
    {
        Directory.CreateDirectory(root);
        var input = await WriteMp3Async("timeout.mp3");
        var processor = await CreateAsync(TimeSpan.FromMilliseconds(750));

        var extraction = processor.ExtractWaveAsync(input);
        await Task.Delay(200);

        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(input, Mp3Bytes()));
        await Assert.ThrowsAsync<TimeoutException>(() => extraction);
    }

    [Fact]
    public async Task ApprovedBindingRevalidatesLicenseBeforeCreatingProcessor()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var license = Path.Combine(root, "LICENSE");
        await File.WriteAllTextAsync(license, "Fixture license.");
        var binding = new ApprovedFfmpegBinding(
            executable,
            await DigestAsync(executable),
            license,
            await DigestAsync(license),
            "NotSigned",
            null,
            null,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(license, "Changed license.");

        Assert.Throws<InvalidOperationException>(() => binding.CreateProcessor(TimeSpan.FromSeconds(5)));
    }

    public void Dispose() => TestDirectory.DeleteIfExists(root);

    private async Task<FfmpegMediaProcessor> CreateAsync(TimeSpan timeout)
    {
        var executable = FixtureExecutable();
        return new(new(executable, await DigestAsync(executable), timeout));
    }

    private async Task<string> WriteMp3Async(string name)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, Mp3Bytes());
        return path;
    }

    private async Task<string> WriteMp4Async(string name)
    {
        var path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path,
        [
            0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0,
        ]);
        return path;
    }

    private static byte[] Mp3Bytes() => [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0];

    private static async Task<string> DigestAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(directory.FullName, "tests", "DCx.FakeWhisperCli", "bin", "Release", "net10.0", "DCx.FakeWhisperCli.exe");
        return File.Exists(executable) ? executable : throw new FileNotFoundException("Build the fixture first.", executable);
    }
}
