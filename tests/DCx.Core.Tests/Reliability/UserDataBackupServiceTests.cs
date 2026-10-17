using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.Reliability;

namespace DCx.Core.Tests.Reliability;

public sealed class UserDataBackupServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-backup-{Guid.NewGuid():N}");

    [Fact]
    public async Task BackupRestore_IsReproducibleAndExcludesBrowserCredentialsAndCaches()
    {
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(data, "Browser"));
        Directory.CreateDirectory(Path.Combine(data, "private", "owner"));
        Directory.CreateDirectory(Path.Combine(data, "WebView2", "Profiles", "Default"));
        await File.WriteAllTextAsync(Path.Combine(data, "Browser", "history.json"), "original history");
        await File.WriteAllTextAsync(Path.Combine(data, "private", "owner", "knowledge.db"), "private knowledge");
        await File.WriteAllTextAsync(Path.Combine(data, "WebView2", "Profiles", "Default", "Cookies"), "excluded credential material");
        var backupOne = Path.Combine(root, "one.dcxbackup");
        var backupTwo = Path.Combine(root, "two.dcxbackup");
        var service = new UserDataBackupService();

        var manifest = await service.CreateAsync(data, backupOne);
        await service.CreateAsync(data, backupTwo);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.DoesNotContain(manifest.Entries, entry => entry.RelativePath.StartsWith("WebView2", StringComparison.Ordinal));
        Assert.Equal(Sha256(backupOne), Sha256(backupTwo));
        await File.WriteAllTextAsync(Path.Combine(data, "Browser", "history.json"), "modified history");
        await File.WriteAllTextAsync(Path.Combine(data, "WebView2", "Profiles", "Default", "Cookies"), "current credential material");
        var restored = await service.RestoreAsync(backupOne, data);

        Assert.Equal(2, restored.FileCount);
        Assert.Equal("original history", await File.ReadAllTextAsync(Path.Combine(data, "Browser", "history.json")));
        Assert.Equal("private knowledge", await File.ReadAllTextAsync(Path.Combine(data, "private", "owner", "knowledge.db")));
        Assert.Equal("current credential material", await File.ReadAllTextAsync(Path.Combine(data, "WebView2", "Profiles", "Default", "Cookies")));
    }

    [Fact]
    public async Task Restore_RejectsTraversalBeforeChangingCurrentData()
    {
        var data = CreateCurrentData();
        var backup = Path.Combine(root, "traversal.dcxbackup");
        await CreateFixtureBackupAsync(
            backup,
            new UserDataBackupEntry("Browser/../WebView2/Cookies", 4, Sha256Bytes("evil"u8.ToArray())),
            "evil"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UserDataBackupService().RestoreAsync(backup, data));
        Assert.Equal("current", await File.ReadAllTextAsync(Path.Combine(data, "Browser", "history.json")));
    }

    [Fact]
    public async Task Restore_RejectsHashMismatchBeforeChangingCurrentData()
    {
        var data = CreateCurrentData();
        var backup = Path.Combine(root, "hash-mismatch.dcxbackup");
        await CreateFixtureBackupAsync(
            backup,
            new UserDataBackupEntry("Browser/history.json", 6, new string('0', 64)),
            "backup"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new UserDataBackupService().RestoreAsync(backup, data));
        Assert.Equal("current", await File.ReadAllTextAsync(Path.Combine(data, "Browser", "history.json")));
    }

    [Fact]
    public async Task BackupArchive_MustRemainOutsideProtectedDataRoot()
    {
        var data = CreateCurrentData();
        var service = new UserDataBackupService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            data,
            Path.Combine(data, "Browser", "nested.dcxbackup")));

        var outside = Path.Combine(root, "outside.dcxbackup");
        await service.CreateAsync(data, outside);
        var inside = Path.Combine(data, "Browser", "restore-source.dcxbackup");
        File.Copy(outside, inside);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(inside, data));
        Assert.Equal("current", await File.ReadAllTextAsync(Path.Combine(data, "Browser", "history.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private string CreateCurrentData()
    {
        var data = Path.Combine(root, "current-data");
        Directory.CreateDirectory(Path.Combine(data, "Browser"));
        File.WriteAllText(Path.Combine(data, "Browser", "history.json"), "current");
        return data;
    }

    private static async Task CreateFixtureBackupAsync(
        string path,
        UserDataBackupEntry manifestEntry,
        byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var payloadEntry = archive.CreateEntry(manifestEntry.RelativePath);
        await using (var output = payloadEntry.Open()) await output.WriteAsync(payload);
        var manifest = archive.CreateEntry(UserDataBackupService.ManifestEntryName);
        await using var manifestOutput = manifest.Open();
        await JsonSerializer.SerializeAsync(
            manifestOutput,
            new UserDataBackupManifest(1, [manifestEntry]),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
