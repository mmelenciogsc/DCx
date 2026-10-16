namespace DCx.Core.OpenAI;

public sealed record CodexAppServerOptions(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    IReadOnlyCollection<string>? SecretEnvironmentVariableNames = null,
    TimeSpan? RequestTimeout = null,
    IReadOnlyCollection<string>? SensitiveValues = null)
{
    public TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(15);
}
