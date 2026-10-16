using DCx.Core.OpenAI;

namespace DCx.Core.Tests.OpenAI;

public sealed class OpenAiAccessPolicyTests
{
    [Fact]
    public void Evaluate_KeepsApiKeyModeIndependentFromCommercialApproval()
    {
        var decision = OpenAiAccessPolicy.Evaluate(hasStoredApiKey: true, commercialChatGptApprovalVerified: false);

        Assert.True(decision.ApiKeyModeAvailable);
        Assert.False(decision.ChatGptPlanModeAvailable);
        Assert.Contains("not been verified", decision.ChatGptPlanReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_DoesNotInventApiKeyAvailability()
    {
        var decision = OpenAiAccessPolicy.Evaluate(hasStoredApiKey: false, commercialChatGptApprovalVerified: true);

        Assert.False(decision.ApiKeyModeAvailable);
        Assert.True(decision.ChatGptPlanModeAvailable);
    }
}
