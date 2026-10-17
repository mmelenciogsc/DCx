using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DCx.Core.GlobalKnowledge;
using DCx.Core.Knowledge;
using DCx.GlobalService;

namespace DCx.Core.Tests.GlobalKnowledge;

public sealed class GlobalServiceE2eTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"dcx-p09-http-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoopbackService_SynchronizesSignedReleaseOverHttp()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKey();
        var trustRoot = key.ExportSubjectPublicKeyInfo();
        var dataRoot = Path.Combine(root, "server");
        await using (var repository = await GlobalRegistryRepository.OpenAsync(dataRoot, privateKey))
        {
            var resource = new GlobalResourceRecord(
                "global:http-fixture",
                "1",
                KnowledgeResourceKind.Ckb,
                "HTTP fixture",
                "A signed release served from the loopback Kestrel simulation.",
                "https://example.test/http-fixture",
                "CC-BY-4.0",
                [],
                [1f]);
            await repository.PublishAsync(
                [new GlobalReleaseChange(GlobalReleaseChangeKind.Upsert, resource.ResourceId, resource.Version, resource)],
                DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        }

        Directory.CreateDirectory(root);
        var keyPath = Path.Combine(root, "simulation-key.pk8");
        await File.WriteAllBytesAsync(keyPath, privateKey);
        var port = ReserveEphemeralPort();
        var endpoint = new Uri($"http://127.0.0.1:{port}/");
        var serviceAssembly = typeof(GlobalRegistryRepository).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(serviceAssembly);
        startInfo.Environment["DCX_GLOBAL_SIMULATION"] = "1";
        startInfo.Environment["DCX_GLOBAL_DATA_ROOT"] = dataRoot;
        startInfo.Environment["DCX_GLOBAL_SIGNING_KEY"] = keyPath;
        startInfo.Environment["DCX_GLOBAL_SIMULATION_URL"] = endpoint.AbsoluteUri;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the local global-service fixture.");
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await WaitForHealthAsync(httpClient, endpoint, process);
            var transport = new HttpGlobalRegistryTransport(httpClient, endpoint);
            var client = await GlobalReplicaClient.OpenAsync(Path.Combine(root, "client"), trustRoot);

            var result = await client.SynchronizeAsync(transport);

            Assert.Equal(1, result.Status.Sequence);
            Assert.Equal("global:http-fixture", Assert.Single(await client.SearchAsync("loopback Kestrel", [1f])).ResourceId);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int ReserveEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHealthAsync(HttpClient client, Uri endpoint, Process process)
    {
        var health = new Uri(endpoint, "health");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"The local global-service fixture exited early: {error}");
            }

            try
            {
                using var response = await client.GetAsync(health);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The loopback global-service fixture did not become healthy.");
    }
}
