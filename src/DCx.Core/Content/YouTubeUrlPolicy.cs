namespace DCx.Core.Content;

public sealed record YouTubeUrlDecision(bool IsAllowed, Uri? CanonicalUri, string Reason);

public sealed class YouTubeUrlPolicy
{
    public YouTubeUrlDecision Evaluate(string input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return Deny("A valid HTTPS YouTube URL is required.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Deny("Credentials are not permitted in a YouTube URL.");
        }

        var host = uri.IdnHost.TrimEnd('.');
        string? videoId = null;
        if (string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        else if (string.Equals(host, "youtube.com", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(host, "m.youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(uri.AbsolutePath.TrimEnd('/'), "/watch", StringComparison.OrdinalIgnoreCase))
            {
                videoId = ParseQuery(uri.Query).GetValueOrDefault("v");
            }
        }

        if (string.IsNullOrWhiteSpace(videoId) || videoId.Length is < 6 or > 32 ||
            videoId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return Deny("Only canonical YouTube watch and youtu.be video URLs are supported.");
        }

        return new YouTubeUrlDecision(
            true,
            new Uri($"https://www.youtube.com/watch?v={Uri.EscapeDataString(videoId)}"),
            "YouTube video URL normalized without tracking parameters.");
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values[Uri.UnescapeDataString(parts[0])] = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }

        return values;
    }

    private static YouTubeUrlDecision Deny(string reason) => new(false, null, reason);
}
