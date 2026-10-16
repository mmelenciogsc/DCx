namespace DCx.Core.OpenAI;

public sealed record CodexModel(string Id, string DisplayName, bool IsDefault, bool IsHidden)
{
    public override string ToString() => DisplayName;
}
