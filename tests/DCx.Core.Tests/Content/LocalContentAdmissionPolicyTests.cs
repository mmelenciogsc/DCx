using DCx.Core.Content;

namespace DCx.Core.Tests.Content;

public sealed class LocalContentAdmissionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dcx-content-{Guid.NewGuid():N}");

    public LocalContentAdmissionPolicyTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("sample.pdf", "%PDF-1.7", ContentKind.Pdf)]
    [InlineData("sample.docx", "PK\x03\x04payload", ContentKind.Word)]
    [InlineData("sample.mp4", "0000ftypisom", ContentKind.Video)]
    [InlineData("sample.txt", "Accessible text", ContentKind.Text)]
    public void Evaluate_AllowsMatchingBoundedFixture(string name, string contents, ContentKind kind)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Decode(contents));

        var result = new LocalContentAdmissionPolicy().Evaluate(path);

        Assert.True(result.IsAllowed);
        Assert.Equal(kind, result.Kind);
    }

    [Fact]
    public void Evaluate_RejectsExtensionSignatureMismatch()
    {
        var path = Path.Combine(_root, "pretend.pdf");
        File.WriteAllText(path, "not a pdf");

        var result = new LocalContentAdmissionPolicy().Evaluate(path);

        Assert.False(result.IsAllowed);
        Assert.Contains("signature", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sample.png", new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })]
    [InlineData("sample.jpg", new byte[] { 0xff, 0xd8, 0xff, 0xe0 })]
    public void Evaluate_AllowsBinaryImageSignatures(string name, byte[] signature)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, signature);

        var result = new LocalContentAdmissionPolicy().Evaluate(path);

        Assert.True(result.IsAllowed);
        Assert.Equal(ContentKind.Image, result.Kind);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static byte[] Decode(string value) => value.Contains("\\x", StringComparison.Ordinal)
        ? [.. value.Replace("\\x03", "\u0003", StringComparison.Ordinal)
            .Replace("\\x04", "\u0004", StringComparison.Ordinal)
            .Select(character => (byte)character)]
        : System.Text.Encoding.ASCII.GetBytes(value);
}
