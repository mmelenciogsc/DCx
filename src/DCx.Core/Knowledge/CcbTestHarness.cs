using System.Security.Cryptography;
using System.Text;
using DCx.Core.Orchestration;

namespace DCx.Core.Knowledge;

public interface IRegisteredCcbTool
{
    string Id { get; }

    string Version { get; }

    string ArtifactSha256 { get; }

    Task<byte[]> ExecuteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken);
}

public sealed record CcbToolTestCase(string Name, byte[] Input, string ExpectedOutputSha256);

public sealed record CcbHarnessResult(
    string ToolId,
    string Version,
    bool Passed,
    string EvidenceSha256,
    string? FailureCode = null);

public sealed class CcbTestHarness
{
    public const int MaximumInputBytes = 1024 * 1024;
    public const int MaximumOutputBytes = 1024 * 1024;

    public async Task<CcbHarnessResult> RunAsync(
        StoredKnowledgeResource resource,
        IRegisteredCcbTool tool,
        IReadOnlyList<CcbToolTestCase> tests,
        TimeSpan perTestTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(tests);
        if (resource.Kind != KnowledgeResourceKind.Ccb ||
            !string.Equals(resource.ResourceId, tool.Id, StringComparison.Ordinal) ||
            !string.Equals(resource.Version, tool.Version, StringComparison.Ordinal) ||
            !string.Equals(resource.ArtifactSha256, tool.ArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
            tests.Count is 0 or > 100 || perTestTimeout <= TimeSpan.Zero || perTestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentException("The registered CCB tool, resource, tests, or timeout are invalid.");
        }

        using var evidence = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        evidence.AppendData(Encoding.UTF8.GetBytes($"{tool.Id}\0{tool.Version}\0{tool.ArtifactSha256}\0"));
        foreach (var test in tests.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(test.Name) || test.Name.Length > 128 ||
                test.Input.Length > MaximumInputBytes || !IsSha256(test.ExpectedOutputSha256))
            {
                return Result(false, "invalid_test_fixture", evidence);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(perTestTimeout);
            byte[] output;
            try
            {
                output = await tool.ExecuteAsync(test.Input, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result(false, "test_timeout", evidence);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return Result(false, "tool_failure", evidence);
            }

            if (output.Length > MaximumOutputBytes)
            {
                return Result(false, "output_limit_exceeded", evidence);
            }

            var actualDigest = SHA256.HashData(output);
            var expectedDigest = Convert.FromHexString(test.ExpectedOutputSha256);
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                return Result(false, "output_mismatch", evidence);
            }

            evidence.AppendData(Encoding.UTF8.GetBytes(test.Name));
            evidence.AppendData(actualDigest);
        }

        return Result(true, null, evidence);

        CcbHarnessResult Result(bool passed, string? failureCode, IncrementalHash hash) =>
            new(tool.Id, tool.Version, passed, Convert.ToHexStringLower(hash.GetHashAndReset()), failureCode);
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

public sealed class VettedCcbCapability : IBossCapability
{
    private readonly StoredKnowledgeResource resource;
    private readonly IRegisteredCcbTool tool;
    private readonly CcbHarnessResult harnessResult;

    public VettedCcbCapability(
        StoredKnowledgeResource resource,
        IRegisteredCcbTool tool,
        CcbHarnessResult harnessResult)
    {
        this.resource = resource ?? throw new ArgumentNullException(nameof(resource));
        this.tool = tool ?? throw new ArgumentNullException(nameof(tool));
        this.harnessResult = harnessResult ?? throw new ArgumentNullException(nameof(harnessResult));
        if (resource.Kind != KnowledgeResourceKind.Ccb ||
            resource.ResourceId != tool.Id || resource.Version != tool.Version ||
            !string.Equals(resource.ArtifactSha256, tool.ArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
            harnessResult.ToolId != tool.Id || harnessResult.Version != tool.Version)
        {
            throw new ArgumentException("The CCB resource, tool and harness evidence do not identify the same version.");
        }
    }

    public string Id => $"ccb:{resource.ResourceId}:{resource.Version}";

    public BossRouteKind RouteKind => BossRouteKind.VettedCcb;

    public bool IsAvailable => harnessResult.Passed;

    public bool Supports(string capability) =>
        resource.Capabilities.Contains(capability, StringComparer.Ordinal);

    public async Task<BossCapabilityResult> ExecuteAsync(
        BossTaskContext context,
        CancellationToken cancellationToken)
    {
        var input = Encoding.UTF8.GetBytes(context.TaskId);
        var output = await tool.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        if (output.Length > CcbTestHarness.MaximumOutputBytes)
        {
            return new BossCapabilityResult(false, new HashSet<string>(), FailureCode: "ccb_output_limit_exceeded");
        }

        IReadOnlySet<string> evidence = new HashSet<string>(StringComparer.Ordinal)
        {
            $"ccb-tested:{resource.ResourceId}:{resource.Version}",
            $"ccb-output-sha256:{Convert.ToHexStringLower(SHA256.HashData(output))}",
        };
        return new BossCapabilityResult(true, evidence);
    }
}
