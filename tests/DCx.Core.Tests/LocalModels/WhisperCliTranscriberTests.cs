using DCx.Core.LocalModels;

namespace DCx.Core.Tests.LocalModels;

public sealed class WhisperCliTranscriberTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-whisper-{Guid.NewGuid():N}");

    [Fact]
    public async Task HashPinnedProcess_TranscribesBoundedWaveAndCleansTemporaryOutput()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.bin");
        var audio = Path.Combine(root, "fixture.wav");
        await File.WriteAllBytesAsync(model, "fixture whisper model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromSeconds(5));

        var result = await transcriber.TranscribeWavAsync(audio);

        Assert.Equal("DCx transcription fixture 42.", result.Text);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(result.PeakWorkingSetBytes is > 0);
    }

    [Fact]
    public async Task InstantProcess_NeverFabricatesZeroByteWorkingSetMeasurement()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.bin");
        var audio = Path.Combine(root, "instant.wav");
        await File.WriteAllBytesAsync(model, "fixture whisper model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromSeconds(5));

        var result = await transcriber.TranscribeWavAsync(audio);

        Assert.Equal("DCx transcription fixture 42.", result.Text);
        Assert.True(result.PeakWorkingSetBytes is null or > 0);
    }

    [Fact]
    public async Task ModelTamperingAndInvalidAudio_FailBeforeProcessExecution()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.gguf");
        var audio = Path.Combine(root, "fixture.wav");
        await File.WriteAllBytesAsync(model, "original model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromSeconds(5));
        await File.WriteAllBytesAsync(model, "tampered model"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => transcriber.TranscribeWavAsync(audio));
        var invalid = Path.Combine(root, "invalid.wav");
        await File.WriteAllTextAsync(invalid, "not a wave");
        await Assert.ThrowsAsync<ArgumentException>(() => transcriber.TranscribeWavAsync(invalid));
    }

    [Fact]
    public async Task Timeout_KillsFixtureProcessAndReturnsNoTranscript()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.bin");
        var audio = Path.Combine(root, "timeout.wav");
        await File.WriteAllBytesAsync(model, "fixture model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromMilliseconds(250));
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(() => transcriber.TranscribeWavAsync(audio));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VerifiedFiles_RemainReadLockedThroughProcessExecution()
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.bin");
        var audio = Path.Combine(root, "timeout.wav");
        await File.WriteAllBytesAsync(model, "fixture model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromMilliseconds(750));

        var transcription = transcriber.TranscribeWavAsync(audio);
        await Task.Delay(200);

        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(model, "replacement"u8.ToArray()));
        await Assert.ThrowsAsync<IOException>(() => File.WriteAllBytesAsync(audio, "replacement"u8.ToArray()));
        await Assert.ThrowsAsync<TimeoutException>(() => transcription);
    }

    [Theory]
    [InlineData("control.wav")]
    [InlineData("invalid-utf8.wav")]
    public async Task UnsafeTranscriptText_FailsClosed(string audioName)
    {
        Directory.CreateDirectory(root);
        var executable = FixtureExecutable();
        var model = Path.Combine(root, "fixture-model.bin");
        var audio = Path.Combine(root, audioName);
        await File.WriteAllBytesAsync(model, "fixture model"u8.ToArray());
        await File.WriteAllBytesAsync(audio, WaveBytes());
        var transcriber = await CreateAsync(executable, model, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => transcriber.TranscribeWavAsync(audio));
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
    }

    private static async Task<WhisperCliTranscriber> CreateAsync(
        string executable,
        string model,
        TimeSpan timeout) =>
        new(new WhisperCliConfiguration(
            executable,
            await WhisperCliTranscriber.ComputeSha256Async(executable),
            model,
            await WhisperCliTranscriber.ComputeSha256Async(model),
            timeout));

    private static byte[] WaveBytes() =>
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 4, 0, 0, 0,
        (byte)'W', (byte)'A', (byte)'V', (byte)'E',
    ];

    private static string FixtureExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DCx.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null) throw new InvalidOperationException("The repository root could not be found.");
        var executable = Path.Combine(
            directory.FullName,
            "tests",
            "DCx.FakeWhisperCli",
            "bin",
            "Release",
            "net10.0",
            "DCx.FakeWhisperCli.exe");
        return File.Exists(executable)
            ? executable
            : throw new FileNotFoundException("Build the fake Whisper CLI fixture before running tests.", executable);
    }
}
