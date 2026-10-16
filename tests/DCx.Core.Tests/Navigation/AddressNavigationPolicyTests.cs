using DCx.Core.Navigation;

namespace DCx.Core.Tests.Navigation;

public sealed class AddressNavigationPolicyTests
{
    private readonly AddressNavigationPolicy _policy = new();

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("https://example.com/path?q=1", "https://example.com/path?q=1")]
    [InlineData("http://localhost:8080/test", "http://localhost:8080/test")]
    public void Evaluate_AllowsHttpAndHttps(string input, string expected)
    {
        var decision = _policy.Evaluate(input);

        Assert.True(decision.IsAllowed);
        Assert.Equal(expected, decision.Uri?.AbsoluteUri);
        Assert.Null(decision.RejectionReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Evaluate_RejectsEmptyAddresses(string? input)
    {
        var decision = _policy.Evaluate(input);

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Uri);
    }

    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("ftp://example.com/file")]
    public void Evaluate_RejectsNonWebSchemes(string input)
    {
        var decision = _policy.Evaluate(input);

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Uri);
        Assert.NotNull(decision.RejectionReason);
    }
}

