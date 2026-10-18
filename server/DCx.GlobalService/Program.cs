using DCx.Core.Finance;
using DCx.Core.GlobalKnowledge;
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
var contributionRateLimiter = contributionApiOptions is null
    ? null
    : new SimulationContributionRequestLimiter(SimulationContributionRateLimitOptions.Load());
var contributionWorkerOptions = SimulationContributionWorkerOptions.Load(
    contributionApiOptions is not null);
var careOperationsApiOptions = SimulationCareOperationsApiOptions.Load();
var careOperationsMonitorOptions = SimulationCareOperationsMonitorOptions.Load(
    careOperationsApiOptions is not null);
var careOperationsRateLimiter = careOperationsApiOptions is null
    ? null
    : new SimulationContributionRequestLimiter(
        new SimulationContributionRateLimitOptions(120, 4));
var careContributorDashboardApiOptions =
    SimulationCareContributorDashboardApiOptions.Load();
var careContributorDashboardRateLimiter = careContributorDashboardApiOptions is null
    ? null
    : new SimulationContributionRequestLimiter(
        new SimulationContributionRateLimitOptions(120, 4));
var inContainer = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
if (inContainer && careContributorDashboardApiOptions is not null)
{
    throw new InvalidOperationException(
        "The CARE contributor dashboard simulation API is restricted to a local loopback process.");
}
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
var admittedSigningKeyPath = GlobalStoragePathGuard.ValidateFile(
    signingKeyPath,
    mustExist: true,
    "global signing key");
var signingKeyFile = new FileInfo(admittedSigningKeyPath);
if (signingKeyFile.Length is <= 0 or > 16 * 1024)
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
    governance = contributionWorkerOptions is null
        ? await ContributionGovernanceService.OpenAsync(governanceRoot, repository, ccbSandbox)
        : await ContributionGovernanceService.OpenForContinuousWorkerAsync(
            governanceRoot, repository, ccbSandbox);
    builder.Services.AddSingleton(governance);
    builder.Services.AddSingleton(contributionApiOptions);
    if (contributionWorkerOptions is not null)
    {
        builder.Services.AddSingleton(contributionWorkerOptions);
        builder.Services.AddHostedService<ContributionPublicationWorker>();
    }
}

DurableCareRepository? careRepository = null;
if (careOperationsApiOptions is not null || careContributorDashboardApiOptions is not null)
{
    careRepository = await DurableCareRepository.OpenAsync(Path.Combine(dataRoot, "care"));
    builder.Services.AddSingleton(careRepository);
}

DurableCareOperationsSource? careOperationsSource = null;
if (careOperationsApiOptions is not null && careRepository is not null)
{
    careOperationsSource = new DurableCareOperationsSource(
        careRepository,
        careOperationsApiOptions.ProcessingEnabled,
        operatorPseudonymSha256: careOperationsApiOptions.OperatorPseudonymSha256);
    builder.Services.AddSingleton(careOperationsSource);
    if (careOperationsMonitorOptions is not null)
    {
        builder.Services.AddSingleton<ICareOperationsSource>(careOperationsSource);
        builder.Services.AddSingleton<ICareOperationsAlertQueue>(careRepository);
        builder.Services.AddSingleton(careOperationsMonitorOptions);
        builder.Services.AddSingleton<ICareOperationsAlertSink, LoggingCareOperationsAlertSink>();
        builder.Services.AddSingleton<CareOperationsMonitor>();
        builder.Services.AddHostedService<CareOperationsMonitoringWorker>();
    }
}

DurableCareContributorDashboardSource? careContributorDashboardSource = null;
if (careContributorDashboardApiOptions is not null && careRepository is not null)
{
    careContributorDashboardSource = new DurableCareContributorDashboardSource(
        careRepository,
        careContributorDashboardApiOptions.ContributorPseudonym);
    builder.Services.AddSingleton(careContributorDashboardSource);
}

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new
{
    status = "simulation",
    contributionApi = contributionApiOptions is not null,
    contributionRateLimit = contributionRateLimiter is not null,
    contributionWorker = contributionWorkerOptions is not null,
    ccbSandbox = ccbSandbox is not null,
    careOperationsApi = careOperationsApiOptions is not null,
    careOperationsProcessing = careOperationsApiOptions?.ProcessingEnabled ?? false,
    careOperationsRateLimit = careOperationsRateLimiter is not null,
    careOperationsMonitor = careOperationsMonitorOptions is not null,
    careContributorDashboardApi = careContributorDashboardApiOptions is not null,
    careContributorDashboardRateLimit = careContributorDashboardRateLimiter is not null,
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

if (contributionApiOptions is not null && contributionRateLimiter is not null && governance is not null)
{
    SimulationContributionApi.Map(
        app,
        governance,
        contributionApiOptions,
        contributionRateLimiter,
        contributionWorkerOptions is not null);
}

if (careOperationsApiOptions is not null && careOperationsRateLimiter is not null &&
    careOperationsSource is not null)
{
    SimulationCareOperationsApi.Map(
        app,
        careOperationsSource,
        careOperationsApiOptions,
        careOperationsRateLimiter);
}

if (careContributorDashboardApiOptions is not null &&
    careContributorDashboardRateLimiter is not null &&
    careContributorDashboardSource is not null)
{
    SimulationCareContributorDashboardApi.Map(
        app,
        careContributorDashboardSource,
        careContributorDashboardApiOptions,
        careContributorDashboardRateLimiter);
}

await app.RunAsync();
