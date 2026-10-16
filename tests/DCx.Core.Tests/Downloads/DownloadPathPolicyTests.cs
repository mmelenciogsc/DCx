using DCx.Core.Downloads;

namespace DCx.Core.Tests.Downloads;

public sealed class DownloadPathPolicyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dcx-download-tests-{Guid.NewGuid():N}");

    [Fact]
    public void GetAvailablePath_RemovesDirectoryTraversal()
    {
        var policy = new DownloadPathPolicy();

        var result = policy.GetAvailablePath(_directory, "..\\..\\report.pdf");

        Assert.Equal(Path.Combine(_directory, "report.pdf"), result);
    }

    [Fact]
    public void GetAvailablePath_AddsSuffixWhenFileExists()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "report.pdf"), "fixture");
        var policy = new DownloadPathPolicy();

        var result = policy.GetAvailablePath(_directory, "report.pdf");

        Assert.Equal(Path.Combine(_directory, "report (1).pdf"), result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

