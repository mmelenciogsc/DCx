namespace DCx.Core.OpenAI;

public sealed record CodexTurnResult(string ThreadId, string TurnId, string Status, string Text);
