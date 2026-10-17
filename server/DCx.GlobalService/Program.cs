using DCx.GlobalService;

if (!string.Equals(Environment.GetEnvironmentVariable("DCX_GLOBAL_SIMULATION"), "1", StringComparison.Ordinal))
{
    throw new InvalidOperationException("The global service is disabled unless DCX_GLOBAL_SIMULATION=1.");
}

var dataRoot = Environment.GetEnvironmentVariable("DCX_GLOBAL_DATA_ROOT");
var signingKeyPath = Environment.GetEnvironmentVariable("DCX_GLOBAL_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(dataRoot) || string.IsNullOrWhiteSpace(signingKeyPath))
{
    throw new InvalidOperationException("DCX_GLOBAL_DATA_ROOT and DCX_GLOBAL_SIGNING_KEY are required.");
}

var builder = WebApplication.CreateBuilder(args);
var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
var simulationUrl = Environment.GetEnvironmentVariable("DCX_GLOBAL_SIMULATION_URL") ?? "http://127.0.0.1:5087";
if (!inContainer &&
    (!Uri.TryCreate(simulationUrl, UriKind.Absolute, out var parsedSimulationUrl) ||
     parsedSimulationUrl.Scheme != Uri.UriSchemeHttp ||
     !System.Net.IPAddress.TryParse(parsedSimulationUrl.Host, out var simulationAddress) ||
     !System.Net.IPAddress.IsLoopback(simulationAddress) ||
     parsedSimulationUrl.AbsolutePath != "/" ||
     !string.IsNullOrEmpty(parsedSimulationUrl.Query) ||
     !string.IsNullOrEmpty(parsedSimulationUrl.Fragment)))
{
    throw new InvalidOperationException("DCX_GLOBAL_SIMULATION_URL must be a loopback HTTP origin.");
}

builder.WebHost.UseUrls(inContainer ? "http://0.0.0.0:5087" : simulationUrl);
var repository = await GlobalRegistryRepository.OpenAsync(dataRoot, await File.ReadAllBytesAsync(signingKeyPath));
builder.Services.AddSingleton(repository);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "simulation" }));
app.MapGet("/api/releases/latest", async (GlobalRegistryRepository registry, CancellationToken cancellationToken) =>
{
    var descriptor = await registry.GetLatestAsync(cancellationToken);
    return descriptor is null ? Results.NotFound() : Results.Ok(descriptor);
});
app.MapGet("/api/releases/{sequence:long}", async (long sequence, GlobalRegistryRepository registry, CancellationToken cancellationToken) =>
    Results.Ok(await registry.GetReleaseAsync(sequence, cancellationToken)));
app.MapGet("/api/releases/{sequence:long}/content", async (
    long sequence,
    long offset,
    int count,
    GlobalRegistryRepository registry,
    CancellationToken cancellationToken) =>
    Results.Bytes(await registry.ReadReleaseChunkAsync(sequence, offset, count, cancellationToken), "application/octet-stream"));
app.MapGet("/api/artifacts/{sha256}", async (
    string sha256,
    long offset,
    int count,
    GlobalRegistryRepository registry,
    CancellationToken cancellationToken) =>
    Results.Bytes(await registry.ReadArtifactChunkAsync(sha256, offset, count, cancellationToken), "application/octet-stream"));

await app.RunAsync();
