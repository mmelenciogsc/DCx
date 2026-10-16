using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();

var portArgument = args
    .Select((value, index) => (value, index))
    .FirstOrDefault(item => string.Equals(item.value, "--port", StringComparison.Ordinal));
var port = portArgument.value is not null && portArgument.index + 1 < args.Length
    ? int.Parse(args[portArgument.index + 1], System.Globalization.CultureInfo.InvariantCulture)
    : 43187;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

var app = builder.Build();
app.MapGet("/health", () => Results.Text("ok", "text/plain"));
app.MapGet("/", () => Results.Content(
    """
    <!doctype html>
    <html lang="en">
    <head><meta charset="utf-8"><title>DCx accessible fixture</title></head>
    <body>
      <main>
        <h1>DCx accessible fixture</h1>
        <label for="approval-code">Approval code</label>
        <input id="approval-code" autocomplete="off">
        <button id="continue">Continue</button>
        <a href="/download">Download fixture</a>
      </main>
    </body>
    </html>
    """,
    "text/html; charset=utf-8"));
app.MapGet("/download", () => Results.File(
    Encoding.UTF8.GetBytes("DCx controlled download fixture.\n"),
    "text/plain",
    "dcx-fixture.txt"));

await app.RunAsync();

