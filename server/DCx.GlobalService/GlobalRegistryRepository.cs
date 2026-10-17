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
        CancellationToken cancellationToken = default) =>
        await PublishCoreAsync(null, changes, issuedUtc, cancellationToken).ConfigureAwait(false);

    public async Task<GlobalReleaseDescriptor> PublishIdempotentAsync(
        string operationId,
        IReadOnlyList<GlobalReleaseChange> changes,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operationId) || operationId.Length > 256 ||
            operationId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not ':'))
        {
            throw new GlobalReplicaException("The publication operation identifier is invalid.");
        }

        return await PublishCoreAsync(operationId, changes, issuedUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GlobalReleaseDescriptor> PublishCoreAsync(
        string? operationId,
        IReadOnlyList<GlobalReleaseChange> changes,
        DateTimeOffset issuedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var requestSha256 = operationId is null
            ? null
            : GlobalReleaseCodec.Sha256(GlobalReleaseCodec.CreateCanonicalChangeSet(changes));
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (operationId is not null)
            {
                var existing = await ReadPublicationOperationAsync(
                    connection, (SqliteTransaction)transaction, operationId, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.RequestSha256, requestSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new GlobalReplicaException("The publication operation identifier was reused for different changes.");
                    }

                    return existing.Descriptor;
                }
            }

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
            if (operationId is not null)
            {
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO publication_operations(operation_id, request_sha256, release_sequence, created_utc)
                    VALUES($operation, $request, $release, $created);
                    """;
                command.Parameters.AddWithValue("$operation", operationId);
                command.Parameters.AddWithValue("$request", requestSha256!);
                command.Parameters.AddWithValue("$release", descriptor.Sequence);
                command.Parameters.AddWithValue("$created", issuedUtc.ToUniversalTime().ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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

    public async Task<GlobalTrustPolicyStatus> StoreTrustPolicyAsync(
        ReadOnlyMemory<byte> policyEnvelope,
        byte[] trustRootSubjectPublicKeyInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustRootSubjectPublicKeyInfo);
        var proposed = GlobalTrustPolicyCodec.Decode(policyEnvelope.Span);
        GlobalTrustPolicyCodec.Verify(proposed, trustRootSubjectPublicKeyInfo);
        var proposedSha256 = GlobalReleaseCodec.Sha256(policyEnvelope.Span);

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            long currentRevision = 0;
            string? currentSha256 = null;
            command.CommandText = "SELECT revision, sha256 FROM trust_policies ORDER BY revision DESC LIMIT 1;";
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    currentRevision = reader.GetInt64(0);
                    currentSha256 = reader.GetString(1);
                }
            }

            if (proposed.Policy.Revision == currentRevision &&
                proposedSha256.Equals(currentSha256, StringComparison.OrdinalIgnoreCase))
            {
                return new GlobalTrustPolicyStatus(
                    proposed.Policy.Revision,
                    proposedSha256,
                    proposed.Policy.ReleaseKeys.Count);
            }

            if (proposed.Policy.Revision != currentRevision + 1 ||
                !string.Equals(
                    proposed.Policy.PreviousPolicySha256,
                    currentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new GlobalReplicaException("The authoritative trust policy does not extend the stored policy chain.");
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO trust_policies(revision, sha256, size_bytes, envelope, issued_utc)
                VALUES($revision, $sha, $size, $envelope, $issued);
                """;
            command.Parameters.AddWithValue("$revision", proposed.Policy.Revision);
            command.Parameters.AddWithValue("$sha", proposedSha256);
            command.Parameters.AddWithValue("$size", policyEnvelope.Length);
            command.Parameters.AddWithValue("$envelope", policyEnvelope.ToArray());
            command.Parameters.AddWithValue("$issued", proposed.Policy.IssuedUtc.ToUniversalTime().ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new GlobalTrustPolicyStatus(
                proposed.Policy.Revision,
                proposedSha256,
                proposed.Policy.ReleaseKeys.Count);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async Task<GlobalTrustPolicyDescriptor?> GetLatestTrustPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT revision, sha256, size_bytes FROM trust_policies ORDER BY revision DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new GlobalTrustPolicyDescriptor(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2));
    }

    public async Task<GlobalTrustPolicyDescriptor> GetTrustPolicyAsync(
        long revision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, size_bytes FROM trust_policies WHERE revision = $revision;";
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GlobalReplicaException("The requested trust-policy revision does not exist.");
        }

        return new GlobalTrustPolicyDescriptor(revision, reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<byte[]> ReadTrustPolicyChunkAsync(
        long revision,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(offset, count);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT sha256, size_bytes, envelope FROM trust_policies WHERE revision = $revision;";
        command.Parameters.AddWithValue("$revision", revision);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new GlobalReplicaException("The requested trust-policy revision does not exist.");
        }

        var expectedSha256 = reader.GetString(0);
        var expectedSize = reader.GetInt64(1);
        var envelope = (byte[])reader[2];
        if (envelope.LongLength != expectedSize ||
            !expectedSha256.Equals(GlobalReleaseCodec.Sha256(envelope), StringComparison.OrdinalIgnoreCase))
        {
            throw new GlobalReplicaException("The authoritative trust-policy envelope failed its stored integrity data.");
        }

        return Slice(envelope, offset, count);
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
            CREATE TABLE IF NOT EXISTS trust_policies(
                revision INTEGER PRIMARY KEY CHECK(revision > 0),
                sha256 TEXT NOT NULL UNIQUE,
                size_bytes INTEGER NOT NULL CHECK(size_bytes > 0),
                envelope BLOB NOT NULL,
                issued_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS publication_operations(
                operation_id TEXT PRIMARY KEY,
                request_sha256 TEXT NOT NULL,
                release_sequence INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                FOREIGN KEY(release_sequence) REFERENCES releases(sequence)
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

    private static async Task<PublicationOperationRow?> ReadPublicationOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation.request_sha256, release.sequence, release.sha256, release.size_bytes, release.envelope
            FROM publication_operations AS operation
            JOIN releases AS release ON release.sequence = operation.release_sequence
            WHERE operation.operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var requestSha256 = reader.GetString(0);
        var envelope = (byte[])reader[4];
        var descriptor = new GlobalReleaseDescriptor(reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3));
        if (requestSha256.Length != 64 || !requestSha256.All(char.IsAsciiHexDigit) ||
            envelope.LongLength != descriptor.SizeBytes ||
            !string.Equals(GlobalReleaseCodec.Sha256(envelope), descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new GlobalReplicaException("The publication operation record failed its integrity check.");
        }

        return new PublicationOperationRow(requestSha256, descriptor);
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

    private sealed record PublicationOperationRow(string RequestSha256, GlobalReleaseDescriptor Descriptor);
}

public sealed class RepositoryRegistryTransport(GlobalRegistryRepository repository) : IGlobalRegistryTransport
{
    public Task<GlobalTrustPolicyDescriptor?> GetLatestTrustPolicyAsync(CancellationToken cancellationToken) =>
        repository.GetLatestTrustPolicyAsync(cancellationToken);

    public Task<GlobalTrustPolicyDescriptor> GetTrustPolicyAsync(
        long revision,
        CancellationToken cancellationToken) =>
        repository.GetTrustPolicyAsync(revision, cancellationToken);

    public Task<byte[]> DownloadTrustPolicyChunkAsync(
        long revision,
        long offset,
        int count,
        CancellationToken cancellationToken) =>
        repository.ReadTrustPolicyChunkAsync(revision, offset, count, cancellationToken);

    public Task<GlobalReleaseDescriptor?> GetLatestReleaseAsync(CancellationToken cancellationToken) =>
        repository.GetLatestAsync(cancellationToken);

    public Task<GlobalReleaseDescriptor> GetReleaseAsync(long sequence, CancellationToken cancellationToken) =>
        repository.GetReleaseAsync(sequence, cancellationToken);

    public Task<byte[]> DownloadReleaseChunkAsync(long sequence, long offset, int count, CancellationToken cancellationToken) =>
        repository.ReadReleaseChunkAsync(sequence, offset, count, cancellationToken);

    public Task<byte[]> DownloadArtifactChunkAsync(string sha256, long offset, int count, CancellationToken cancellationToken) =>
        repository.ReadArtifactChunkAsync(sha256, offset, count, cancellationToken);
}
