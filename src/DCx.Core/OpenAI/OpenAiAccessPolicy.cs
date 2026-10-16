namespace DCx.Core.OpenAI;

public sealed record OpenAiAccessDecision(
    bool ApiKeyModeAvailable,
    bool ChatGptPlanModeAvailable,
    string ChatGptPlanReason);

public static class OpenAiAccessPolicy
{
    public static OpenAiAccessDecision Evaluate(bool hasStoredApiKey, bool commercialChatGptApprovalVerified) =>
        new(
            hasStoredApiKey,
            commercialChatGptApprovalVerified,
            commercialChatGptApprovalVerified
                ? "Commercial ChatGPT-plan approval is recorded."
                : "Unavailable: OpenAI approval for commercial ChatGPT-plan distribution has not been verified.");
}
