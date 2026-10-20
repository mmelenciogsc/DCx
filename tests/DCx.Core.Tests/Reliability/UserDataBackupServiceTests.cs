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
        var verified = await service.VerifyAsync(backupOne);

        Assert.Equal(2, manifest.Entries.Count);
        Assert.Equal(2, verified.FileCount);
        Assert.Equal("original history".Length + "private knowledge".Length, verified.TotalBytes);
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
    public async Task Verify_IsReadOnlyAndIndependentOfApplicationDataLease()
    {
        var data = CreateCurrentData();
        var backup = Path.Combine(root, "verify-read-only.dcxbackup");
        var service = new UserDataBackupService();
        await service.CreateAsync(data, backup);
        var hashBefore = Sha256(backup);
        var lengthBefore = new FileInfo(backup).Length;
        var writeBefore = File.GetLastWriteTimeUtc(backup);

        UserDataBackupVerificationResult verified;
        using (ApplicationDataOperationLease.Acquire(data, "running DCx"))
        {
            verified = await service.VerifyAsync(backup);
        }

        Assert.Equal(1, verified.FileCount);
        Assert.Equal("current".Length, verified.TotalBytes);
        Assert.Equal(hashBefore, Sha256(backup));
        Assert.Equal(lengthBefore, new FileInfo(backup).Length);
        Assert.Equal(writeBefore, File.GetLastWriteTimeUtc(backup));
        Assert.Equal("current", await File.ReadAllTextAsync(Path.Combine(data, "Browser", "history.json")));
    }

    [Fact]
    public async Task Verify_RejectsDuplicateJsonUnknownFieldsAndDuplicatePayloadPaths()
    {
        var payload = "backup"u8.ToArray();
        var sha256 = Sha256Bytes(payload);
        var duplicateJson = Path.Combine(root, "duplicate-json.dcxbackup");
        var unknownJson = Path.Combine(root, "unknown-json.dcxbackup");
        var duplicatePayload = Path.Combine(root, "duplicate-payload.dcxbackup");
        var entryJson = $"{{\"relativePath\":\"Browser/history.json\",\"length\":{payload.Length},\"sha256\":\"{sha256}\"}}";
        await CreateRawManifestBackupAsync(
            duplicateJson,
            $"{{\"schema\":1,\"schema\":1,\"entries\":[{entryJson}]}}",
            payload,
            duplicatePayloadEntry: false);
        await CreateRawManifestBackupAsync(
            unknownJson,
            $"{{\"schema\":1,\"entries\":[{entryJson}],\"unexpected\":true}}",
            payload,
            duplicatePayloadEntry: false);
        await CreateRawManifestBackupAsync(
            duplicatePayload,
            $"{{\"schema\":1,\"entries\":[{entryJson}]}}",
            payload,
            duplicatePayloadEntry: true);

        var service = new UserDataBackupService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(duplicateJson));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(unknownJson));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.VerifyAsync(duplicatePayload));
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

    [Fact]
    public void OperationLease_DeniesConcurrentUseAndReleasesAfterDispose()
    {
        var data = Path.Combine(root, "lease-data");
        using (ApplicationDataOperationLease.Acquire(data, "running DCx"))
        {
            var exception = Assert.Throws<ApplicationDataInUseException>(() =>
                ApplicationDataOperationLease.Acquire(data, "creating a backup"));
            Assert.Contains("Close DCx", exception.Message, StringComparison.Ordinal);
        }

        using var reopened = ApplicationDataOperationLease.Acquire(data, "creating a backup");
        Assert.Equal(Path.GetFullPath(data), reopened.DataRoot);
    }

    [Fact]
    public void OperationLease_RejectsFilesystemRoot()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetFullPath(root))!;
        Assert.Throws<InvalidOperationException>(() =>
            ApplicationDataOperationLease.Acquire(filesystemRoot, "creating a backup"));
    }

    public void Dispose()
    {
        TestDirectory.DeleteIfExists(root);
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

    private static async Task CreateRawManifestBackupAsync(
        string path,
        string manifestJson,
        byte[] payload,
        bool duplicatePayloadEntry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        var payloadEntry = archive.CreateEntry("Browser/history.json");
        await using (var output = payloadEntry.Open()) await output.WriteAsync(payload);
        if (duplicatePayloadEntry)
        {
            var duplicateEntry = archive.CreateEntry("Browser/history.json");
            await using var duplicateOutput = duplicateEntry.Open();
            await duplicateOutput.WriteAsync(payload);
        }

        var manifest = archive.CreateEntry(UserDataBackupService.ManifestEntryName);
        await using var manifestOutput = manifest.Open();
        await manifestOutput.WriteAsync(System.Text.Encoding.UTF8.GetBytes(manifestJson));
    }

    private static string Sha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
