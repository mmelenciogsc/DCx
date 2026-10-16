namespace DCx.Core.LocalModels;

public sealed record LocalModelDescriptor(
    string Name,
    long SizeBytes,
    bool SupportsVision,
    bool SupportsEmbeddings,
    string? ParameterSize = null,
    string? Quantization = null,
    string? License = null);

public sealed record LocalModelSelection(LocalModelDescriptor? Model, string Reason)
{
    public bool IsAvailable => Model is not null;
}
