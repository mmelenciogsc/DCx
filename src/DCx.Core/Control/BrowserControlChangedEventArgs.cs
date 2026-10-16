namespace DCx.Core.Control;

public sealed class BrowserControlChangedEventArgs(
    BrowserControlMode previousMode,
    BrowserControlMode currentMode,
    string reason) : EventArgs
{
    public BrowserControlMode PreviousMode { get; } = previousMode;

    public BrowserControlMode CurrentMode { get; } = currentMode;

    public string Reason { get; } = reason;
}

