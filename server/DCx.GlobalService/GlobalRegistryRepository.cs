using System.Security.Cryptography;
using DCx.Core.GlobalKnowledge;
using Microsoft.Data.Sqlite;

namespace DCx.GlobalService;

public sealed class GlobalRegistryRepository : IAsyncDisposable
{
    private const int MaximumArtifactBytes = 256 * 1024 * 1024;

    private readonly string connectionString;
    private readonly string artifactsRoot;
    private readonly ECDsa signingKey;
    private readonly byte[] trustRoot;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    private GlobalRegistryRepository(string root, byte[] privateKeyPkcs8)
    {
        Root = root;
        artifactsRoot = Path.Combine(root, "artifacts", "sha256");
        Directory.CreateDirectory(artifactsRoot);
        signingKey = ECDsa.Create();
        signingKey.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
        if (bytesRead != privateKeyPkcs8.Length)
        {
            throw new CryptographicException("The registry signing key contains trailing data.");
        }

        trustRoot = signingKey.ExportSubjectPublicKeyInfo();

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "authoritative-registry.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public string Root { get; }

    public byte[] TrustRootSubjectPublicKeyInfo => trustRoot.ToArray();

    public static async Task<GlobalRegistryRepository> OpenAsync(
        string root,
        byte[] privateKeyPkcs8,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(privateKeyPkcs8);
        var canonical = Path.GetFullPath(root);
        Directory.CreateDirectory(canonical);
        if ((File.GetAttributes(canonical) & FileAttributes.ReparsePoint) != 0)
        {
            throw new GlobalReplicaException("The authoritative registry root cannot be a reparse point.");
        }

        var repository = new GlobalRegistryRepository(canonical, privateKeyPkcs8);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return repository;
    }

    public async Task<GlobalArtifactReference> AddArtifactAsync(
        ReadOnlyMemory<byte> content,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty || content.Length > MaximumArtifactBytes ||
            string.IsNullOrWhiteSpace(mediaType) || mediaType.Length > 256)
        {
            throw new GlobalReplicaException("The authoritative artifact content or media type is invalid.");
        }

        var digest = GlobalReleaseCodec.Sha256(content.Span);
        var directory = Path.Combine(artifactsRoot, digest[..2]);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, digest);
        if (!File.Exists(destination))
        {
            var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, content.ToArray(), cancellationToken).ConfigureAwait(false);
                try
                {
                    File.Move(temporary, destination, overwrite: false);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        else
        {
            await using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var existingDigest = await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false);
            if (existing.Length != content.Length ||
                !CryptographicOperations.FixedTimeEquals(existingDigest, Convert.FromHexString(digest)))
            {
                throw new GlobalReplicaException("An existing authoritative artifact failed its integrity check.");
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts(sha256, size_bytes, media_type, relative_path)
            VALUES($sha, $size, $media, $path)
            ON CONFLICT(sha256) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$sha", digest);
        command.Parameters.AddWithValue("$size", content.Length);
        command.Parameters.AddWithValue("$media", mediaType);
        command.Parameters.AddWithValue("$path", Path.GetRelativePath(Root, destination));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = "SELECT size_bytes, media_type FROM artifacts WHERE sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", digest);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != content.Length ||
            !string.Equals(reader.GetString(1), mediaType, StringComparison.Ordinal))
        {
            throw new GlobalReplicaException("The authoritative artifact metadata conflicts with the existing digest.");
        }

        return new GlobalArtifactReference(digest, content.Length, mediaType);
    }

    public async Task<GlobalReleaseDescriptor> PublishAsync(
        IReadOnlyList<GlobalReleaseChange> changes,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var previous = await ReadLatestRowAsync(connection, (SqliteTransaction)transaction, cancellationToken)
                .ConfigureAwait(false);
            var previousResources = previous is null
                ? Array.Empty<GlobalResourceRecord>()
                : GlobalReleaseCodec.DecodeCatalog(previous.CatalogBytes).ToArray();
            foreach (var artifact in changes
                         .Where(change => change.Kind == GlobalReleaseChangeKind.Upsert)
                         .Select(change => change.Resource?.Artifact)
                         .Where(reference => reference is not null))
            {
                await EnsureArtifactAsync(connection, (SqliteTransaction)transaction, artifact!, cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = GlobalCatalogReducer.ApplyChanges(previousResources, changes);
            var catalogBytes = GlobalReleaseCodec.CreateCanonicalCatalog(result);
            var sequence = (previous?.Descriptor.Sequence ?? 0) + 1;
            var manifest = new GlobalReleaseManifest(
                GlobalReleaseCodec.CurrentSchemaVersion,
                sequence,
                sequence - 1,
                previous?.Descriptor.Sha256,
                GlobalReleaseCodec.Sha256(catalogBytes),
                issuedUtc.ToUniversalTime(),
                changes);
            GlobalReleaseCodec.Validate(manifest);
            _ = GlobalCatalogReducer.Apply(previousResources, manifest);
            var envelopeBytes = GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(manifest, signingKey));
            var descriptor = new GlobalReleaseDescriptor(sequence, GlobalReleaseCodec.Sha256(envelopeBytes), envelopeBytes.Length);

            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO releases(sequence, sha256, size_bytes, envelope, catalog, issued_utc)
                VALUES($sequence, $sha, $size, $envelope, $catalog, $issued);
                """;
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$sha", descriptor.Sha256);
            command.Parameters.AddWithValue("$size", descriptor.SizeBytes);
            command.Parameters.AddWithValue("$envelope", envelopeBytes);
            command.Parameters.AddWithValue("$catalog", catalogBytes);
            command.Parameters.AddWithValue("$issued", issuedUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return descriptor;
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<GlobalReleaseDescriptor?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadLatestRowAsync(connection, null, cancellationToken).ConfigureAwait(false);
        return row?.Descriptor;
    }

    public async Task<GlobalReleaseDescriptor> GetReleaseAsync(
        long sequence,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, size_bytes FROM releases WHERE sequence = $sequence;";
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GlobalReplicaException("The requested registry release does not exist.");
        }

        return new GlobalReleaseDescriptor(sequence, reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<byte[]> ReadReleaseChunkAsync(
        long sequence,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(offset, count);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT envelope FROM releases WHERE sequence = $sequence;";
        command.Parameters.AddWithValue("$sequence", sequence);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
            ?? throw new GlobalReplicaException("The requested registry release does not exist.");
        return Slice(value, offset, count);
    }

    public async Task<byte[]> ReadArtifactChunkAsync(
        string sha256,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new GlobalReplicaException("The requested registry artifact digest is invalid.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT size_bytes, relative_path FROM artifacts WHERE sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", sha256.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GlobalReplicaException("The requested registry artifact does not exist.");
        }

        var size = reader.GetInt64(0);
        var path = Path.GetFullPath(Path.Combine(Root, reader.GetString(1)));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(path).Length != size)
        {
            throw new GlobalReplicaException("The authoritative artifact path or size is invalid.");
        }

        ValidateRange(offset, count);
        if (offset >= size)
        {
            throw new GlobalReplicaException("The requested artifact range is invalid.");
        }

        var length = (int)Math.Min(count, size - offset);
        var buffer = new byte[length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    public ValueTask DisposeAsync()
    {
        signingKey.Dispose();
        writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS releases(
                sequence INTEGER PRIMARY KEY,
                sha256 TEXT NOT NULL UNIQUE,
                size_bytes INTEGER NOT NULL,
                envelope BLOB NOT NULL,
                catalog BLOB NOT NULL,
                issued_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifacts(
                sha256 TEXT PRIMARY KEY,
                size_bytes INTEGER NOT NULL,
                media_type TEXT NOT NULL,
                relative_path TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<ReleaseRow?> ReadLatestRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sequence, sha256, size_bytes, catalog FROM releases ORDER BY sequence DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ReleaseRow(
            new GlobalReleaseDescriptor(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)),
            (byte[])reader[3]);
    }

    private static byte[] Slice(byte[] value, long offset, int count)
    {
        ValidateRange(offset, count);
        if (offset >= value.Length)
        {
            throw new GlobalReplicaException("The requested release range is invalid.");
        }

        return value.AsSpan((int)offset, Math.Min(count, value.Length - (int)offset)).ToArray();
    }

    private static async Task EnsureArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GlobalArtifactReference reference,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT size_bytes, media_type, relative_path FROM artifacts WHERE sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", reference.Sha256.ToLowerInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetInt64(0) != reference.SizeBytes ||
            !string.Equals(reader.GetString(1), reference.MediaType, StringComparison.Ordinal))
        {
            throw new GlobalReplicaException("A release references an absent or conflicting authoritative artifact.");
        }

        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(connection.DataSource)!, reader.GetString(2)));
        if (!File.Exists(path) || new FileInfo(path).Length != reference.SizeBytes)
        {
            throw new GlobalReplicaException("A release references an unavailable authoritative artifact file.");
        }
    }

    private static void ValidateRange(long offset, int count)
    {
        if (offset < 0 || count is < 1 or > GlobalReplicaClient.TransferChunkBytes)
        {
            throw new GlobalReplicaException("The requested registry range is invalid.");
        }
    }

    private sealed record ReleaseRow(GlobalReleaseDescriptor Descriptor, byte[] CatalogBytes);
}

public sealed class RepositoryRegistryTransport(GlobalRegistryRepository repository) : IGlobalRegistryTransport
{
    public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
        repository.GetLatestAsync(cancellationToken);

    public Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken) =>
        repository.GetReleaseAsync(sequence, cancellationToken);

    public Task<byte[]> DownloadReleaseChunkAsync(long sequence, long offset, int count, CancellationToken cancellationToken) =>
        repository.ReadReleaseChunkAsync(sequence, offset, count, cancellationToken);

    public Task<byte[]> DownloadArtifactChunkAsync(string sha256, long offset, int count, CancellationToken cancellationToken) =>
        repository.ReadArtifactChunkAsync(sha256, offset, count, cancellationToken);
}
