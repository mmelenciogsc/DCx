namespace DCx.Core.LocalModels;

public sealed record LocalModelDescriptor(
    string Name,
    long SizeBytes,
    bool SupportsVision,
    bool SupportsEmbeddings,
    string? ParameterSize = null,
    string? Quantization = null,
    string? License = null,
    string? Digest = null)
{
    public bool HasDigestBoundName =>
        Digest is { Length: 64 } &&
        Digest.All(character => char.IsAsciiHexDigit(character)) &&
        Name.Length > Digest.Length &&
        Name[Name.Length - Digest.Length - 1] is ':' or '-' &&
        Name.EndsWith(Digest, StringComparison.OrdinalIgnoreCase);
}

public sealed record LocalModelSelection(LocalModelDescriptor? Model, string Reason)
{
    public bool IsAvailable => Model is not null;
}
