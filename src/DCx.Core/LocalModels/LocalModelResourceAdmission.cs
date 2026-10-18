namespace DCx.Core.LocalModels;

internal interface ILocalModelResourceAdmission
{
    void EnsureVisionAllowed(LocalModelDescriptor model);

    void EnsureEmbeddingAllowed(LocalModelDescriptor model);
}

internal sealed class WindowsLocalModelResourceAdmission : ILocalModelResourceAdmission
{
    private readonly LocalHardwareProfiler profiler;
    private readonly LocalModelSelector selector;
    private readonly string applicationDataPath;

    private WindowsLocalModelResourceAdmission(
        LocalHardwareProfiler profiler,
        LocalModelSelector selector,
        string applicationDataPath)
    {
        this.profiler = profiler;
        this.selector = selector;
        this.applicationDataPath = applicationDataPath;
    }

    internal static WindowsLocalModelResourceAdmission CreateForCurrentUser()
    {
        var applicationDataPath = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(applicationDataPath) || !Path.IsPathFullyQualified(applicationDataPath))
        {
            throw new InvalidOperationException(
                "The current-user application-data volume is unavailable for local-model resource admission.");
        }
        return new WindowsLocalModelResourceAdmission(
            new LocalHardwareProfiler(),
            new LocalModelSelector(),
            Path.GetFullPath(applicationDataPath));
    }

    internal WindowsLocalModelResourceAdmission(
        LocalHardwareProfiler profiler,
        string applicationDataPath)
        : this(
            profiler ?? throw new ArgumentNullException(nameof(profiler)),
            new LocalModelSelector(),
            Path.GetFullPath(applicationDataPath ?? throw new ArgumentNullException(nameof(applicationDataPath))))
    {
    }

    public void EnsureVisionAllowed(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var selection = selector.SelectForHardware(profiler.Capture(applicationDataPath), [model]).Vision.Model;
        if (!Matches(selection, model))
        {
            throw new InvalidOperationException(
                "The approved vision model no longer fits the current usable GPU-memory safety budget.");
        }
    }

    public void EnsureEmbeddingAllowed(LocalModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var selection = selector.SelectForHardware(profiler.Capture(applicationDataPath), [model]).Embedding.Model;
        if (!Matches(selection, model))
        {
            throw new InvalidOperationException(
                "The approved embedding model no longer fits the current available system-memory safety budget.");
        }
    }

    private static bool Matches(LocalModelDescriptor? candidate, LocalModelDescriptor expected) =>
        candidate is not null &&
        string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal) &&
        string.Equals(candidate.Digest, expected.Digest, StringComparison.OrdinalIgnoreCase);
}

internal sealed class UncheckedLocalModelResourceAdmission : ILocalModelResourceAdmission
{
    internal static UncheckedLocalModelResourceAdmission Instance { get; } = new();

    private UncheckedLocalModelResourceAdmission()
    {
    }

    public void EnsureVisionAllowed(LocalModelDescriptor model) => ArgumentNullException.ThrowIfNull(model);

    public void EnsureEmbeddingAllowed(LocalModelDescriptor model) => ArgumentNullException.ThrowIfNull(model);
}
