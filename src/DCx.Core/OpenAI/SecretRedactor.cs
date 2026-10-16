using System.Text.RegularExpressions;

namespace DCx.Core.OpenAI;

public sealed class SecretRedactor(IEnumerable<string>? knownSecrets = null)
{
    private static readonly Regex BearerPattern = new(
        @"(?i)(authorization\s*:\s*bearer\s+)[^\s,;]+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly string[] _knownSecrets = (knownSecrets ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(value => value.Length)
        .ToArray();

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = BearerPattern.Replace(text, "$1[REDACTED]");
        foreach (var secret in _knownSecrets)
        {
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return redacted;
    }
}
