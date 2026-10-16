namespace DCx.Core.Orchestration;

public sealed record BossTaskBudget(int MaximumAttempts, int MaximumTokens, TimeSpan MaximumElapsed)
{
    public void Validate()
    {
        if (MaximumAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAttempts), "Attempts must be between 1 and 10.");
        }

        if (MaximumTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumTokens));
        }

        if (MaximumElapsed <= TimeSpan.Zero || MaximumElapsed > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumElapsed), "Elapsed budget must be positive and at most one hour.");
        }
    }
}
