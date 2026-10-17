using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DCx.Core.Knowledge;

public sealed partial class PrivateKnowledgeStore
{
    private const int SchemaVersion = 1;
    private const int MaximumBodyCharacters = 2 * 1024 * 1024;
    private const int MaximumEmbeddingDimensions = 4096;

    private readonly string connectionString;
    private readonly CcbProvenanceVerifier provenanceVerifier;

    private PrivateKnowledgeStore(
        string profileId,
        string storeRoot,
        CcbProvenanceVerifier provenanceVerifier)
    {
        ProfileId = profileId;
        StoreRoot = storeRoot;
        this.provenanceVerifier = provenanceVerifier;
        ArtifactStore = new ContentAddressedArtifactStore(storeRoot);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storeRoot, "private-knowledge.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    public string ProfileId { get; }

    public string StoreRoot { get; }

    public ContentAddressedArtifactStore ArtifactStore { get; }

    public static async Task<PrivateKnowledgeStore> OpenAsync(
        string applicationDataRoot,
        string profileId,
        IEnumerable<TrustedCcbPublisher>? trustedPublishers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        ValidateIdentifier(profileId, nameof(profileId), 128);
        var ownerHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profileId)));
        var root = Path.GetFullPath(Path.Combine(applicationDataRoot, "private", ownerHash));
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new KnowledgeArtifactException("A private knowledge root cannot be a reparse point.");
        }

        var store = new PrivateKnowledgeStore(
            profileId,
            root,
            new CcbProvenanceVerifier(trustedPublishers ?? []));
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task PutAsync(
        KnowledgeResourceDraft draft,
        string? expectedPreviousVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        provenanceVerifier.Verify(draft);
        if (draft.ArtifactSha256 is not null)
        {
            var artifactPath = ArtifactStore.ResolveExisting(draft.ArtifactSha256);
            if (draft.Kind == KnowledgeResourceKind.Ccb)
            {
                VerifyCcbPackageIdentity(artifactPath, draft.ResourceId, draft.Version);
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var currentVersion = await ReadCurrentVersionAsync(connection, transaction, draft.ResourceId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentVersion, expectedPreviousVersion, StringComparison.Ordinal))
        {
            throw new KnowledgeVersionConflictException(
                $"Resource {draft.ResourceId} changed from the expected version.");
        }

        await InsertVersionAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false);
        await ReplaceHeadAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false);
        await ReplaceFtsAsync(connection, transaction, draft, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredKnowledgeResource?> GetAsync(
        string resourceId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(resourceId, nameof(resourceId), 128);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var resource = await ReadResourceAsync(connection, resourceId, cancellationToken).ConfigureAwait(false);
        VerifyStoredResource(resource);
        return resource;
    }

    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchAsync(
        string query,
        float[]? queryEmbedding,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        ValidateEmbedding(queryEmbedding, nameof(queryEmbedding));
        var matchExpression = CreateMatchExpression(query);
        if (matchExpression is null && queryEmbedding is null)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sparse = await SearchTextAsync(connection, matchExpression, cancellationToken).ConfigureAwait(false);
        var vectors = queryEmbedding is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : await SearchVectorsAsync(connection, queryEmbedding, cancellationToken).ConfigureAwait(false);
        var identifiers = sparse.Keys.Concat(vectors.Keys).Distinct(StringComparer.Ordinal);
        var ranked = new List<KnowledgeSearchResult>();
        foreach (var resourceId in identifiers)
        {
            var resource = await ReadResourceAsync(connection, resourceId, cancellationToken).ConfigureAwait(false);
            if (resource is null)
            {
                continue;
            }

            VerifyStoredResource(resource);

            var hasSparse = sparse.TryGetValue(resourceId, out var sparseScore);
            var hasVector = vectors.TryGetValue(resourceId, out var vectorScore);
            var score = hasSparse && hasVector
                ? (sparseScore * 0.55) + (vectorScore * 0.45)
                : hasSparse ? sparseScore : vectorScore;
            ranked.Add(new KnowledgeSearchResult(resource, score));
        }

        return ranked
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Resource.ResourceId, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = FULL;
            CREATE TABLE IF NOT EXISTS store_identity (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                profile_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS resource_versions (
                resource_id TEXT NOT NULL,
                version TEXT NOT NULL,
                kind INTEGER NOT NULL CHECK (kind IN (0, 1)),
                title TEXT NOT NULL,
                body TEXT NOT NULL,
                source_uri TEXT NOT NULL,
                license TEXT NOT NULL,
                artifact_sha256 TEXT,
                publisher_id TEXT,
                provenance_signature TEXT,
                created_utc TEXT NOT NULL,
                PRIMARY KEY (resource_id, version)
            );
            CREATE TABLE IF NOT EXISTS resource_heads (
                resource_id TEXT PRIMARY KEY,
                current_version TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS resource_capabilities (
                resource_id TEXT NOT NULL,
                version TEXT NOT NULL,
                capability TEXT NOT NULL,
                PRIMARY KEY (resource_id, version, capability),
                FOREIGN KEY (resource_id, version) REFERENCES resource_versions(resource_id, version) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS resource_dependencies (
                resource_id TEXT NOT NULL,
                version TEXT NOT NULL,
                dependency_id TEXT NOT NULL,
                version_constraint TEXT NOT NULL,
                PRIMARY KEY (resource_id, version, dependency_id),
                FOREIGN KEY (resource_id, version) REFERENCES resource_versions(resource_id, version) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS resource_vectors (
                resource_id TEXT NOT NULL,
                version TEXT NOT NULL,
                dimensions INTEGER NOT NULL,
                vector BLOB NOT NULL,
                PRIMARY KEY (resource_id, version),
                FOREIGN KEY (resource_id, version) REFERENCES resource_versions(resource_id, version) ON DELETE CASCADE
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS resource_fts USING fts5(
                resource_id UNINDEXED,
                version UNINDEXED,
                title,
                body,
                tokenize = 'unicode61'
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "INSERT OR IGNORE INTO store_identity(singleton, profile_id, schema_version) VALUES(1, $profile, $schema);";
        command.Parameters.AddWithValue("$profile", ProfileId);
        command.Parameters.AddWithValue("$schema", SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.Parameters.Clear();
        command.CommandText = "SELECT profile_id, schema_version FROM store_identity WHERE singleton = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            !string.Equals(reader.GetString(0), ProfileId, StringComparison.Ordinal) ||
            reader.GetInt32(1) != SchemaVersion)
        {
            throw new InvalidDataException("The private knowledge store identity or schema does not match.");
        }
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

    private static async Task<string?> ReadCurrentVersionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT current_version FROM resource_heads WHERE resource_id = $id;";
        command.Parameters.AddWithValue("$id", resourceId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        KnowledgeResourceDraft draft,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO resource_versions(
                resource_id, version, kind, title, body, source_uri, license,
                artifact_sha256, publisher_id, provenance_signature, created_utc)
            VALUES($id, $version, $kind, $title, $body, $source, $license, $artifact, $publisher, $signature, $created);
            """;
        command.Parameters.AddWithValue("$id", draft.ResourceId);
        command.Parameters.AddWithValue("$version", draft.Version);
        command.Parameters.AddWithValue("$kind", (int)draft.Kind);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$body", draft.Body);
        command.Parameters.AddWithValue("$source", draft.SourceUri);
        command.Parameters.AddWithValue("$license", draft.License);
        command.Parameters.AddWithValue("$artifact", (object?)draft.ArtifactSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$publisher", (object?)draft.Provenance?.PublisherId ?? DBNull.Value);
        command.Parameters.AddWithValue("$signature", (object?)draft.Provenance?.SignatureBase64 ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new KnowledgeVersionConflictException("The resource version already exists.");
        }

        foreach (var capability in draft.Capabilities.Distinct(StringComparer.Ordinal))
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO resource_capabilities(resource_id, version, capability) VALUES($id, $version, $value);";
            command.Parameters.AddWithValue("$id", draft.ResourceId);
            command.Parameters.AddWithValue("$version", draft.Version);
            command.Parameters.AddWithValue("$value", capability);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var dependency in draft.Dependencies)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO resource_dependencies(resource_id, version, dependency_id, version_constraint) VALUES($id, $version, $dependency, $constraint);";
            command.Parameters.AddWithValue("$id", draft.ResourceId);
            command.Parameters.AddWithValue("$version", draft.Version);
            command.Parameters.AddWithValue("$dependency", dependency.ResourceId);
            command.Parameters.AddWithValue("$constraint", dependency.VersionConstraint);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (draft.Embedding is not null)
        {
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO resource_vectors(resource_id, version, dimensions, vector) VALUES($id, $version, $dimensions, $vector);";
            command.Parameters.AddWithValue("$id", draft.ResourceId);
            command.Parameters.AddWithValue("$version", draft.Version);
            command.Parameters.AddWithValue("$dimensions", draft.Embedding.Length);
            command.Parameters.AddWithValue("$vector", EncodeVector(draft.Embedding));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceHeadAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        KnowledgeResourceDraft draft,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO resource_heads(resource_id, current_version) VALUES($id, $version)
            ON CONFLICT(resource_id) DO UPDATE SET current_version = excluded.current_version;
            """;
        command.Parameters.AddWithValue("$id", draft.ResourceId);
        command.Parameters.AddWithValue("$version", draft.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceFtsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        KnowledgeResourceDraft draft,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM resource_fts WHERE resource_id = $id;";
        command.Parameters.AddWithValue("$id", draft.ResourceId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        command.CommandText = "INSERT INTO resource_fts(resource_id, version, title, body) VALUES($id, $version, $title, $body);";
        command.Parameters.AddWithValue("$version", draft.Version);
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$body", draft.Body);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredKnowledgeResource?> ReadResourceAsync(
        SqliteConnection connection,
        string resourceId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.version, v.kind, v.title, v.body, v.source_uri, v.license,
                   v.artifact_sha256, v.publisher_id, v.provenance_signature
            FROM resource_heads h
            JOIN resource_versions v ON v.resource_id = h.resource_id AND v.version = h.current_version
            WHERE h.resource_id = $id;
            """;
        command.Parameters.AddWithValue("$id", resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = reader.GetString(0);
        var resource = new StoredKnowledgeResource(
            resourceId,
            version,
            (KnowledgeResourceKind)reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            [],
            [],
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) || reader.IsDBNull(8)
                ? null
                : new CcbProvenanceEnvelope(reader.GetString(7), reader.GetString(8)));
        await reader.DisposeAsync().ConfigureAwait(false);

        var capabilities = await ReadCapabilitiesAsync(connection, resourceId, version, cancellationToken).ConfigureAwait(false);
        var dependencies = await ReadDependenciesAsync(connection, resourceId, version, cancellationToken).ConfigureAwait(false);
        return resource with { Capabilities = capabilities, Dependencies = dependencies };
    }

    private void VerifyStoredResource(StoredKnowledgeResource? resource)
    {
        if (resource is null || resource.Kind != KnowledgeResourceKind.Ccb)
        {
            return;
        }

        if (resource.ArtifactSha256 is null)
        {
            throw new KnowledgeArtifactException("The stored CCB resource has no artifact digest.");
        }

        VerifyCcbPackageIdentity(
            ArtifactStore.ResolveExisting(resource.ArtifactSha256),
            resource.ResourceId,
            resource.Version);
        provenanceVerifier.Verify(new KnowledgeResourceDraft(
            resource.ResourceId,
            resource.Version,
            resource.Kind,
            resource.Title,
            resource.Body,
            resource.SourceUri,
            resource.License,
            resource.Capabilities,
            resource.Dependencies,
            ArtifactSha256: resource.ArtifactSha256,
            Provenance: resource.Provenance));
    }

    private static void VerifyCcbPackageIdentity(string artifactPath, string resourceId, string version)
    {
        var manifest = new CcbPackageValidator().ReadManifest(File.ReadAllBytes(artifactPath));
        if (!string.Equals(manifest.Id, resourceId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Version, version, StringComparison.Ordinal))
        {
            throw new KnowledgeArtifactException("The signed CCB metadata does not match its package manifest.");
        }
    }

    private static async Task<IReadOnlyList<string>> ReadCapabilitiesAsync(
        SqliteConnection connection,
        string resourceId,
        string version,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT capability FROM resource_capabilities WHERE resource_id = $id AND version = $version ORDER BY capability;";
        command.Parameters.AddWithValue("$id", resourceId);
        command.Parameters.AddWithValue("$version", version);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<IReadOnlyList<KnowledgeDependency>> ReadDependenciesAsync(
        SqliteConnection connection,
        string resourceId,
        string version,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT dependency_id, version_constraint FROM resource_dependencies WHERE resource_id = $id AND version = $version ORDER BY dependency_id;";
        command.Parameters.AddWithValue("$id", resourceId);
        command.Parameters.AddWithValue("$version", version);
        var values = new List<KnowledgeDependency>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new KnowledgeDependency(reader.GetString(0), reader.GetString(1)));
        }

        return values;
    }

    private static async Task<Dictionary<string, double>> SearchTextAsync(
        SqliteConnection connection,
        string? matchExpression,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (matchExpression is null)
        {
            return scores;
        }

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.resource_id
            FROM resource_fts f
            JOIN resource_heads h ON h.resource_id = f.resource_id AND h.current_version = f.version
            WHERE resource_fts MATCH $query
            ORDER BY bm25(resource_fts)
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$query", matchExpression);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rank = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scores[reader.GetString(0)] = 1d / (1d + rank++);
        }

        return scores;
    }

    private static async Task<Dictionary<string, double>> SearchVectorsAsync(
        SqliteConnection connection,
        float[] queryEmbedding,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.resource_id, v.vector
            FROM resource_vectors v
            JOIN resource_heads h ON h.resource_id = v.resource_id AND h.current_version = v.version
            WHERE v.dimensions = $dimensions
            LIMIT 5000;
            """;
        command.Parameters.AddWithValue("$dimensions", queryEmbedding.Length);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var similarity = CosineSimilarity(queryEmbedding, DecodeVector((byte[])reader[1]));
            scores[reader.GetString(0)] = (similarity + 1d) / 2d;
        }

        return scores;
    }

    private static string? CreateMatchExpression(string query)
    {
        var terms = SearchTermRegex().Matches(query ?? string.Empty)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"")
            .ToArray();
        return terms.Length == 0 ? null : string.Join(" OR ", terms);
    }

    private static void ValidateDraft(KnowledgeResourceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateIdentifier(draft.ResourceId, nameof(draft.ResourceId), 128);
        ValidateIdentifier(draft.Version, nameof(draft.Version), 64);
        ValidateText(draft.Title, nameof(draft.Title), 512);
        ValidateText(draft.Body, nameof(draft.Body), MaximumBodyCharacters);
        ValidateText(draft.SourceUri, nameof(draft.SourceUri), 2048);
        ValidateText(draft.License, nameof(draft.License), 256);
        if (draft.Capabilities.Count > 64 || draft.Dependencies.Count > 128)
        {
            throw new ArgumentException("The resource contains too many capabilities or dependencies.");
        }

        foreach (var capability in draft.Capabilities)
        {
            ValidateIdentifier(capability, nameof(draft.Capabilities), 128);
        }

        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in draft.Dependencies)
        {
            ValidateIdentifier(dependency.ResourceId, nameof(draft.Dependencies), 128);
            ValidateText(dependency.VersionConstraint, nameof(draft.Dependencies), 128);
            if (!dependencyIds.Add(dependency.ResourceId) || dependency.ResourceId == draft.ResourceId)
            {
                throw new ArgumentException("Dependencies must be unique and cannot reference the resource itself.");
            }
        }

        ValidateEmbedding(draft.Embedding, nameof(draft.Embedding));
        if (draft.ArtifactSha256 is not null &&
            (draft.ArtifactSha256.Length != 64 || !draft.ArtifactSha256.All(char.IsAsciiHexDigit)))
        {
            throw new ArgumentException("The artifact SHA-256 is malformed.", nameof(draft));
        }

        if (draft.Kind == KnowledgeResourceKind.Ccb && draft.Capabilities.Count == 0)
        {
            throw new ArgumentException("A CCB resource must declare at least one capability.", nameof(draft));
        }
    }

    private static void ValidateEmbedding(float[]? embedding, string parameterName)
    {
        if (embedding is null)
        {
            return;
        }

        if (embedding.Length is 0 or > MaximumEmbeddingDimensions ||
            embedding.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("The embedding has invalid dimensions or values.", parameterName);
        }
    }

    private static void ValidateIdentifier(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':' or '/')))
        {
            throw new ArgumentException("The identifier is empty, too long, or contains unsupported characters.", parameterName);
        }
    }

    private static void ValidateText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Contains('\0'))
        {
            throw new ArgumentException("The value is empty, too long, or contains a null character.", parameterName);
        }
    }

    private static byte[] EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] DecodeVector(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static double CosineSimilarity(float[] left, float[] right)
    {
        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        return leftMagnitude == 0 || rightMagnitude == 0
            ? 0
            : dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}_-]{2,64}", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermRegex();
}
