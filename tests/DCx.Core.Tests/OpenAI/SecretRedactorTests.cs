using DCx.Core.OpenAI;

namespace DCx.Core.Tests.OpenAI;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_RemovesKnownSecretAndBearerCredential()
    {
        const string secret = "fixture-secret-value-123456789";
        var redactor = new SecretRedactor([secret]);

        var output = redactor.Redact($"value={secret}; Authorization: Bearer another-token");

        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("another-token", output, StringComparison.Ordinal);
        Assert.Equal(2, output.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }
}
