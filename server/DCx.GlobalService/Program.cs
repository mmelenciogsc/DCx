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
var contributionApiOptions = SimulationContributionApiOptions.Load();
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

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = DCx.Core.Contributions.ContributionTransportCodec.MaximumEnvelopeBytes;
});
builder.WebHost.UseUrls(inContainer ? "http://0.0.0.0:5087" : simulationUrl);
var signingKeyFile = new FileInfo(Path.GetFullPath(signingKeyPath));
if (!signingKeyFile.Exists || signingKeyFile.Length is <= 0 or > 16 * 1024 ||
    (signingKeyFile.Attributes & FileAttributes.ReparsePoint) != 0)
{
    throw new InvalidOperationException("The global signing-key file is not an admitted regular file.");
}

byte[] signingKeyBytes;
await using (var signingKeyStream = new FileStream(
                 signingKeyFile.FullName,
                 FileMode.Open,
                 FileAccess.Read,
                 FileShare.Read,
                 4096,
                 FileOptions.Asynchronous | FileOptions.SequentialScan))
{
    if (signingKeyStream.Length is <= 0 or > 16 * 1024)
    {
        throw new InvalidOperationException("The global signing-key file changed during admission.");
    }

    signingKeyBytes = GC.AllocateUninitializedArray<byte>((int)signingKeyStream.Length);
    await signingKeyStream.ReadExactlyAsync(signingKeyBytes);
}
GlobalRegistryRepository repository;
try
{
    repository = await GlobalRegistryRepository.OpenAsync(dataRoot, signingKeyBytes);
}
finally
{
    System.Security.Cryptography.CryptographicOperations.ZeroMemory(signingKeyBytes);
}
builder.Services.AddSingleton(repository);
ContributionGovernanceService? governance = null;
DCx.Core.Contributions.ICcbSandboxExecutor? ccbSandbox = null;
if (contributionApiOptions is not null)
{
    var governanceRoot = Path.Combine(dataRoot, "governance");
    ccbSandbox = SimulationCcbSandboxConfiguration.Load(governanceRoot);
    governance = await ContributionGovernanceService.OpenAsync(
        governanceRoot, repository, ccbSandbox);
    builder.Services.AddSingleton(governance);
    builder.Services.AddSingleton(contributionApiOptions);
}

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new
{
    status = "simulation",
    contributionApi = contributionApiOptions is not null,
    ccbSandbox = ccbSandbox is not null,
}));
app.MapGet("/api/trust-policy/latest", async (GlobalRegistryRepository registry, CancellationToken cancellationToken) =>
{
    var descriptor = await registry.GetLatestTrustPolicyAsync(cancellationToken);
    return descriptor is null ? Results.NotFound() : Results.Ok(descriptor);
});
app.MapGet("/api/trust-policy/{revision:long}", async (
    long revision,
    GlobalRegistryRepository registry,
    CancellationToken cancellationToken) =>
    Results.Ok(await registry.GetTrustPolicyAsync(revision, cancellationToken)));
app.MapGet("/api/trust-policy/{revision:long}/content", async (
    long revision,
    long offset,
    int count,
    GlobalRegistryRepository registry,
    CancellationToken cancellationToken) =>
    Results.Bytes(
        await registry.ReadTrustPolicyChunkAsync(revision, offset, count, cancellationToken),
        "application/octet-stream"));
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

if (contributionApiOptions is not null && governance is not null)
{
    SimulationContributionApi.Map(app, governance, contributionApiOptions);
}

await app.RunAsync();
