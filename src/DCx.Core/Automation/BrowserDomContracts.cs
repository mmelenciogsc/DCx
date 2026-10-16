namespace DCx.Core.Automation;

public enum BrowserDomActionKind
{
    FocusById,
    ClickById,
    ReadValueById,
}

public sealed record BrowserDomAction(BrowserDomActionKind Kind, string ElementId);

public sealed record BrowserDomActionResult(bool Succeeded, string? Value, string? Error);

public sealed record BrowserDomElementSnapshot(
    string Tag,
    string? Id,
    string? Role,
    string? Name,
    string? Type,
    bool Disabled);

