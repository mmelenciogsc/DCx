using DCx.Core.Content;

namespace DCx.Core.Tests.Content;

public sealed class YouTubeUrlPolicyTests
{
    [Theory]
    [InlineData("https://youtu.be/AbCdEf12345?t=9")]
    [InlineData("https://www.youtube.com/watch?v=AbCdEf12345&utm_source=test")]
    public void Evaluate_NormalizesSupportedVideoUrlAndDropsTracking(string input)
    {
        var result = new YouTubeUrlPolicy().Evaluate(input);

        Assert.True(result.IsAllowed);
        Assert.Equal("https://www.youtube.com/watch?v=AbCdEf12345", result.CanonicalUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://youtube.com/watch?v=AbCdEf12345")]
    [InlineData("https://evil.example/watch?v=AbCdEf12345")]
    [InlineData("https://youtube.com.evil.example/watch?v=AbCdEf12345")]
    [InlineData("https://user:password@youtube.com/watch?v=AbCdEf12345")]
    [InlineData("https://youtube.com/results?search_query=test")]
    public void Evaluate_RejectsUnsafeOrUnsupportedUrl(string input)
    {
        Assert.False(new YouTubeUrlPolicy().Evaluate(input).IsAllowed);
    }
}
