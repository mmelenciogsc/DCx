using System.IO;

namespace DCx.Desktop.OpenAI;

public sealed record CodexRuntimeDescriptor(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    bool IsTestFixture);

public static class CodexRuntimeLocator
{
    public static CodexRuntimeDescriptor? Find(string repositoryRoot)
    {
        var testMode = string.Equals(
            Environment.GetEnvironmentVariable("DCX_TEST_MODE"),
            "1",
            StringComparison.Ordinal);
        var testServer = Environment.GetEnvironmentVariable("DCX_CODEX_TEST_SERVER");
        if (testMode && !string.IsNullOrWhiteSpace(testServer))
        {
            var fullTestPath = Path.GetFullPath(testServer);
            var fullRepositoryRoot = EnsureTrailingSeparator(Path.GetFullPath(repositoryRoot));
            if (fullTestPath.StartsWith(fullRepositoryRoot, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(fullTestPath), "DCx.FakeCodexAppServer.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(fullTestPath))
            {
                return new CodexRuntimeDescriptor(fullTestPath, [], IsTestFixture: true);
            }
        }

        var configured = Environment.GetEnvironmentVariable("DCX_CODEX_EXECUTABLE");
        var configuredRuntime = ValidateProductionExecutable(configured);
        if (configuredRuntime is not null)
        {
            return configuredRuntime;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = ValidateProductionExecutable(Path.Combine(directory, "codex.exe"));
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static CodexRuntimeDescriptor? ValidateProductionExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        return string.Equals(Path.GetFileName(fullPath), "codex.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath)
            ? new CodexRuntimeDescriptor(
                fullPath,
                [
                    "app-server",
                    "--listen",
                    "stdio://",
                    "-c",
                    "cli_auth_credentials_store=\"ephemeral\"",
                    "-c",
                    "forced_login_method=\"api\"",
                    "-c",
                    "web_search=\"disabled\"",
                    "-c",
                    "features.apps=false",
                    "-c",
                    "features.multi_agent=false",
                    "-c",
                    "shell_environment_policy.ignore_default_excludes=false",
                ],
                IsTestFixture: false)
            : null;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
