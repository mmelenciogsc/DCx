using System.Collections.Concurrent;
using System.Text.Json;

var outputGate = new SemaphoreSlim(1, 1);
var activeTurns = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
var configuredSecret = Environment.GetEnvironmentVariable("DCX_FAKE_SECRET");
var configuredTurnResponseDelay = Environment.GetEnvironmentVariable("DCX_FAKE_TURN_RESPONSE_DELAY_MS");
var turnResponseDelayMilliseconds = int.TryParse(configuredTurnResponseDelay, out var parsedTurnResponseDelay) &&
    parsedTurnResponseDelay is >= 1 and <= 1_000
        ? parsedTurnResponseDelay
        : 0;
if (!string.IsNullOrEmpty(configuredSecret))
{
    Console.Error.WriteLine($"diagnostic Authorization: Bearer {configuredSecret}");
}

async Task WriteAsync(object message)
{
    await outputGate.WaitAsync();
    try
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
        await Console.Out.FlushAsync();
    }
    finally
    {
        outputGate.Release();
    }
}

async Task RunTurnAsync(string threadId, string turnId, CancellationTokenSource turnCancellation)
{
    await WriteAsync(new { method = "turn/started", @params = new { threadId, turn = new { id = turnId, status = "inProgress" } } });
    try
    {
        foreach (var delta in new[] { "Fixture ", "response ", "completed." })
        {
            await Task.Delay(120, turnCancellation.Token);
            await WriteAsync(new
            {
                method = "item/agentMessage/delta",
                @params = new { threadId, turnId, itemId = "item-1", delta },
            });
        }

        await WriteAsync(new { method = "turn/completed", @params = new { threadId, turn = new { id = turnId, status = "completed" } } });
    }
    catch (OperationCanceledException)
    {
        await WriteAsync(new { method = "turn/completed", @params = new { threadId, turn = new { id = turnId, status = "interrupted" } } });
    }
    finally
    {
        activeTurns.TryRemove(turnId, out _);
        turnCancellation.Dispose();
    }
}

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    var method = root.GetProperty("method").GetString();
    var hasId = root.TryGetProperty("id", out var id);
    switch (method)
    {
        case "initialize":
            await WriteAsync(new { id = id.GetInt64(), result = new { userAgent = "dcx-fake/1", platformFamily = "windows", platformOs = "windows" } });
            break;
        case "initialized":
            break;
        case "model/list":
            await WriteAsync(new
            {
                id = id.GetInt64(),
                result = new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "fixture-model",
                            model = "fixture-model",
                            displayName = "Offline fixture model",
                            hidden = false,
                            isDefault = true,
                        },
                    },
                    nextCursor = (string?)null,
                },
            });
            break;
        case "account/login/start":
            await WriteAsync(new { id = id.GetInt64(), result = new { type = "apiKey" } });
            break;
        case "thread/start":
            await WriteAsync(new { id = id.GetInt64(), result = new { thread = new { id = $"thread-{Guid.NewGuid():N}" } } });
            break;
        case "turn/start":
        {
            var threadId = root.GetProperty("params").GetProperty("threadId").GetString()!;
            var turnId = $"turn-{Guid.NewGuid():N}";
            var cancellation = new CancellationTokenSource();
            activeTurns[turnId] = cancellation;
            if (turnResponseDelayMilliseconds > 0)
            {
                _ = RunTurnAsync(threadId, turnId, cancellation);
                await Task.Delay(turnResponseDelayMilliseconds);
                await WriteAsync(new { id = id.GetInt64(), result = new { turn = new { id = turnId, status = "inProgress" } } });
                break;
            }
            await WriteAsync(new { id = id.GetInt64(), result = new { turn = new { id = turnId, status = "inProgress" } } });
            _ = RunTurnAsync(threadId, turnId, cancellation);
            break;
        }
        case "turn/interrupt":
        {
            var turnId = root.GetProperty("params").GetProperty("turnId").GetString()!;
            if (activeTurns.TryGetValue(turnId, out var cancellation))
            {
                cancellation.Cancel();
            }

            await WriteAsync(new { id = id.GetInt64(), result = new { } });
            break;
        }
        default:
            if (hasId)
            {
                await WriteAsync(new { id = id.GetInt64(), error = new { code = -32601, message = "Method not found" } });
            }

            break;
    }
}
