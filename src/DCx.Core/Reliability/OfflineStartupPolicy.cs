namespace DCx.Core.Reliability;

public static class OfflineStartupPolicy
{
    public const string EnvironmentVariable = "DCX_OFFLINE_STARTUP";
    public const string DocumentAddress = "about:blank";

    public const string DocumentHtml = """
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>DCx offline</title></head>
        <body>
          <main>
            <h1>DCx is ready offline</h1>
            <p>No website was contacted during startup. Enter an address when connectivity is available, or use local files and private knowledge.</p>
          </main>
        </body>
        </html>
        """;

    public static bool IsEnabled(string? value) =>
        string.Equals(value?.Trim(), "1", StringComparison.Ordinal) ||
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsOfflineDocument(string? address) =>
        string.Equals(address, DocumentAddress, StringComparison.OrdinalIgnoreCase);

    public static bool IsInitialInternalNavigation(string? address) =>
        IsOfflineDocument(address) ||
        address?.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase) == true;
}
