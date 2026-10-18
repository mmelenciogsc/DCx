using System.Net;
using System.Net.Sockets;
using System.Text;

if (args.Length != 1 || !string.Equals(args[0], "serve", StringComparison.Ordinal)) return 2;
if (Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty)
    .Contains("exit", StringComparison.OrdinalIgnoreCase)) return 3;

var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");
if (string.IsNullOrWhiteSpace(host) || !host.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
    !int.TryParse(host.AsSpan("127.0.0.1:".Length), out var port)) return 4;

var expected = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["OLLAMA_NO_CLOUD"] = "1",
    ["OLLAMA_NOPRUNE"] = "1",
    ["OLLAMA_KEEP_ALIVE"] = "0s",
    ["OLLAMA_MAX_LOADED_MODELS"] = "1",
    ["OLLAMA_NUM_PARALLEL"] = "1",
    ["OLLAMA_MAX_QUEUE"] = "1",
    ["OLLAMA_MAX_TRANSFER_STREAMS"] = "1",
    ["OLLAMA_ORIGINS"] = "http://127.0.0.1",
    ["OLLAMA_VULKAN"] = "0",
};
var secure = expected.All(item => string.Equals(
    Environment.GetEnvironmentVariable(item.Key), item.Value, StringComparison.Ordinal)) &&
    Directory.Exists(Environment.GetEnvironmentVariable("OLLAMA_MODELS")) &&
    Directory.Exists(Environment.GetEnvironmentVariable("TEMP")) &&
    string.Equals(
        Environment.GetEnvironmentVariable("USERPROFILE"),
        Environment.GetEnvironmentVariable("TEMP"),
        StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DCX_FORBIDDEN_AMBIENT_SECRET"));

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start(8);
while (true)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
    var requestLine = await reader.ReadLineAsync();
    string? line;
    do { line = await reader.ReadLineAsync(); } while (!string.IsNullOrEmpty(line));
    var body = requestLine switch
    {
        "GET /api/version HTTP/1.1" => $"{{\"version\":\"{(secure ? "fixture-secure" : "fixture-unsafe")}\"}}",
        "GET /api/tags HTTP/1.1" => "{\"models\":[]}",
        _ => "{}",
    };
    var status = requestLine is "GET /api/version HTTP/1.1" or "GET /api/tags HTTP/1.1"
        ? "200 OK"
        : "404 Not Found";
    var bytes = Encoding.UTF8.GetBytes(body);
    var header = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
    await stream.WriteAsync(header);
    await stream.WriteAsync(bytes);
}
