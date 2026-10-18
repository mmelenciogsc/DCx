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
        await using var client = CreateClient(delayTurnStartResponse: true);
        await client.StartAsync();
        using var cancellation = new CancellationTokenSource();

        var pending = client.RunTurnAsync(
            "fixture-model",
            "Cancel this fixture turn.",
            cancellation.Token);
        using var activeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (client.ActiveTurnCount != 1)
        {
            await Task.Delay(10, activeTimeout.Token);
        }
        cancellation.Cancel();

        var result = await pending;

        Assert.Equal("interrupted", result.Status);
        Assert.Equal(0, client.ActiveTurnCount);
    }

    [Fact]
    public async Task Client_RejectsResponseIdentityThatConflictsWithExactTurnNotification()
    {
        await using var client = CreateClient(
            delayTurnStartResponse: true,
            mismatchTurnStartResponse: true);
        await client.StartAsync();

        var error = await Assert.ThrowsAsync<CodexProtocolException>(() => client.RunTurnAsync(
            "fixture-model",
            "Reject a conflicting turn identity."));

        Assert.Contains("conflicting turn identities", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.ActiveTurnCount);
    }

    private static CodexAppServerClient CreateClient(
        bool delayTurnStartResponse = false,
        bool mismatchTurnStartResponse = false)
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
            new Dictionary<string, string?>
            {
                ["DCX_FAKE_SECRET"] = FixtureSecret,
                ["DCX_FAKE_TURN_RESPONSE_DELAY_MS"] = delayTurnStartResponse ? "750" : null,
                ["DCX_FAKE_TURN_RESPONSE_MISMATCH"] = mismatchTurnStartResponse ? "1" : null,
            },
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
