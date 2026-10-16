using DCx.Core.OpenAI;

namespace DCx.Core.Tests.OpenAI;

public sealed class CodexAppServerClientTests
{
    private const string FixtureSecret = "fixture-secret-value-123456789";

    [Fact]
    public async Task Client_HandshakesListsModelsCompletesAndReconnects()
    {
        await using var client = CreateClient();

        await client.StartAsync();
        await client.LoginWithApiKeyAsync(FixtureSecret);
        var firstProcessId = client.ProcessId;
        var models = await client.ListModelsAsync();
        var result = await client.RunTurnAsync(models.Single().Id, "Offline fixture request.");

        Assert.True(client.IsReady);
        Assert.Equal("fixture-model", models.Single().Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("Fixture response completed.", result.Text);

        await client.RestartAsync();

        Assert.True(client.IsReady);
        Assert.NotEqual(firstProcessId, client.ProcessId);
        Assert.Single(await client.ListModelsAsync());
        Assert.DoesNotContain(FixtureSecret, client.Diagnostics, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", client.Diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_CancellationInterruptsOnlyActiveTurn()
    {
        await using var client = CreateClient();
        await client.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await client.RunTurnAsync("fixture-model", "Cancel this fixture turn.", cancellation.Token);

        Assert.Equal("interrupted", result.Status);
    }

    private static CodexAppServerClient CreateClient()
    {
        var repositoryRoot = FindRepositoryRoot();
        var executable = Path.Combine(
            repositoryRoot,
            "tests",
            "DCx.FakeCodexAppServer",
            "bin",
            "Release",
            "net10.0",
            "DCx.FakeCodexAppServer.exe");
        return new CodexAppServerClient(new CodexAppServerOptions(
            executable,
            [],
            repositoryRoot,
            new Dictionary<string, string?> { ["DCX_FAKE_SECRET"] = FixtureSecret },
            ["DCX_FAKE_SECRET"],
            TimeSpan.FromSeconds(5)));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DCx.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("The DCx repository root was not found.");
    }
}
