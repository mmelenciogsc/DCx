using System.Security.Cryptography;
using System.Text.Json;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;

var issued = DateTimeOffset.Parse("2026-10-17T00:00:00Z");
using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var resources = Enumerable.Range(0, 1000)
    .Select(index => Resource(index, "1", embedding: null))
    .ToArray();
var initialCatalog = GlobalReleaseCodec.CreateCanonicalCatalog(resources);
var initialChanges = resources
    .Select(resource => new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource))
    .ToArray();
var initialManifest = new GlobalReleaseManifest(
    1,
    1,
    0,
    null,
    GlobalReleaseCodec.Sha256(initialCatalog),
    issued,
    initialChanges);
var initialEnvelope = GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(initialManifest, signingKey));

var vectorResources = Enumerable.Range(0, 1000)
    .Select(index => Resource(index, "1", Enumerable.Range(0, 384).Select(dimension => (float)((index + dimension) % 19) / 19).ToArray()))
    .ToArray();
var vectorCatalog = GlobalReleaseCodec.CreateCanonicalCatalog(vectorResources);

var updates = resources.Take(100)
    .Select(resource =>
    {
        var updated = resource with { Version = "2", Summary = resource.Summary + " Updated." };
        return new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, updated.ResourceId, updated.Version, updated);
    })
    .ToArray();
var updatedCatalog = GlobalCatalogReducer.ApplyChanges(resources, updates);
var updatedCatalogBytes = GlobalReleaseCodec.CreateCanonicalCatalog(updatedCatalog);
var incrementalManifest = new GlobalReleaseManifest(
    1,
    2,
    1,
    GlobalReleaseCodec.Sha256(initialEnvelope),
    GlobalReleaseCodec.Sha256(updatedCatalogBytes),
    issued.AddDays(1),
    updates);
var incrementalEnvelope = GlobalReleaseCodec.Encode(GlobalReleaseCodec.Sign(incrementalManifest, signingKey));

var result = new
{
    fixture_resources = 1000,
    metadata_catalog_bytes = initialCatalog.Length,
    initial_release_bytes = initialEnvelope.Length,
    catalog_with_384_dimension_vectors_bytes = vectorCatalog.Length,
    incremental_changes = 100,
    incremental_release_bytes = incrementalEnvelope.Length,
    retained_two_catalogs_and_releases_bytes =
        initialCatalog.Length + initialEnvelope.Length + updatedCatalogBytes.Length + incrementalEnvelope.Length,
    artifact_policy = "Artifacts are excluded and downloaded on use; add exact signed artifact sizes to storage forecasts.",
};
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

static GlobalResourceRecord Resource(int index, string version, float[]? embedding) =>
    new(
        $"global:fixture-{index:D4}",
        version,
        KnowledgeResourceKind.Ckb,
        $"Fixture resource {index}",
        $"Deterministic metadata forecast resource {index}. {new string('x', 180)}",
        $"https://example.test/resources/{index}",
        "CC-BY-4.0",
        [],
        embedding);
