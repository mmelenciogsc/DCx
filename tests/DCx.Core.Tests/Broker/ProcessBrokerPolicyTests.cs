using DCx.Core.Broker;

namespace DCx.Core.Tests.Broker;

public sealed class ProcessBrokerPolicyTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"dcx-broker-policy-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"dcx-broker-outside-{Guid.NewGuid():N}");

    public ProcessBrokerPolicyTests()
    {
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_workspace, "fixture.txt"), "fixture");
        File.WriteAllText(Path.Combine(_outside, "outside.txt"), "outside");
    }

    [Theory]
    [InlineData(BrokerOperation.LaunchControlledFixture)]
    [InlineData(BrokerOperation.LaunchCalculator)]
    public void Evaluate_AllowsArgumentFreeAllowlistedApplications(BrokerOperation operation)
    {
        var policy = new ProcessBrokerPolicy(_workspace);

        var result = policy.Evaluate(new BrokerRequest(operation));

        Assert.True(result.IsAllowed);
        Assert.Null(result.CanonicalTargetPath);
    }

    [Fact]
    public void Evaluate_AllowsCanonicalWorkspaceInspection()
    {
        var policy = new ProcessBrokerPolicy(_workspace);
        var path = Path.Combine(_workspace, ".", "fixture.txt");

        var result = policy.Evaluate(new BrokerRequest(BrokerOperation.InspectPathReadOnly, path));

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.Combine(_workspace, "fixture.txt"), result.CanonicalTargetPath);
    }

    [Theory]
    [InlineData(BrokerOperation.LaunchNotepad)]
    [InlineData(BrokerOperation.InspectPathReadOnly)]
    public void Evaluate_DeniesTraversalOutsideWorkspace(BrokerOperation operation)
    {
        var policy = new ProcessBrokerPolicy(_workspace);
        var outside = Path.Combine(_workspace, "..", "outside.txt");

        var result = policy.Evaluate(new BrokerRequest(operation, outside));

        Assert.False(result.IsAllowed);
        Assert.Contains("outside", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_DeniesUnexpectedArgumentsForAllowlistedApplication()
    {
        var policy = new ProcessBrokerPolicy(_workspace);

        var result = policy.Evaluate(new BrokerRequest(BrokerOperation.LaunchCalculator, "& whoami"));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void Evaluate_DeniesMissingInspectionTarget()
    {
        var policy = new ProcessBrokerPolicy(_workspace);

        var result = policy.Evaluate(new BrokerRequest(
            BrokerOperation.InspectPathReadOnly,
            Path.Combine(_workspace, "missing.txt")));

        Assert.False(result.IsAllowed);
    }

    [Fact]
    public void Evaluate_DeniesWorkspaceSymlinkThatEscapesToExistingTarget()
    {
        var policy = new ProcessBrokerPolicy(_workspace);
        var link = Path.Combine(_workspace, "outside-link");
        Directory.CreateSymbolicLink(link, _outside);

        var result = policy.Evaluate(new BrokerRequest(
            BrokerOperation.InspectPathReadOnly,
            Path.Combine(link, "outside.txt")));

        Assert.False(result.IsAllowed);
        Assert.Contains("junction", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }

        if (Directory.Exists(_outside))
        {
            Directory.Delete(_outside, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
