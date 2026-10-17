using System.Text.RegularExpressions;

namespace DCx.Core.Contributions;

public sealed partial class ContributionScanner
{
    private static readonly IReadOnlySet<string> AllowedLicenses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "CC0-1.0", "CC-BY-4.0", "MPL-2.0",
    };

    public ContributionScanResult Scan(ContributionCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var findings = new List<ContributionFinding>();
        var content = $"{candidate.Title}\n{candidate.Body}";
        AddIf(PrivateKeyRegex().IsMatch(content), "secret.private_key", ContributionFindingSeverity.Reject);
        AddIf(ApiCredentialRegex().IsMatch(content), "secret.api_credential", ContributionFindingSeverity.Reject);
        AddIf(EmailRegex().IsMatch(content), "pii.email", ContributionFindingSeverity.Quarantine);
        AddIf(SocialSecurityRegex().IsMatch(content), "pii.government_identifier", ContributionFindingSeverity.Reject);
        AddIf(PhoneRegex().IsMatch(content), "pii.phone", ContributionFindingSeverity.Quarantine);
        AddIf(MaliciousCommandRegex().IsMatch(content), "code.suspicious_command", ContributionFindingSeverity.Quarantine);
        AddIf(candidate.Body.Trim().Length < 40 || DummyRegex().IsMatch(candidate.Body.Trim()),
            "quality.dummy_or_empty", ContributionFindingSeverity.Quarantine);
        AddIf(candidate.Title.Length > 512 || candidate.Body.Length > 64 * 1024,
            "content.size_limit", ContributionFindingSeverity.Reject);
        AddIf(!AllowedLicenses.Contains(candidate.License), "license.unapproved", ContributionFindingSeverity.Quarantine);
        AddIf(candidate.Kind == Knowledge.KnowledgeResourceKind.Ccb && string.IsNullOrWhiteSpace(candidate.ArtifactSha256),
            "artifact.ccb_missing", ContributionFindingSeverity.Reject);
        AddIf(candidate.ArtifactSha256 is not null &&
              (candidate.ArtifactSha256.Length != 64 || !candidate.ArtifactSha256.All(char.IsAsciiHexDigit)),
            "artifact.digest_invalid", ContributionFindingSeverity.Reject);

        return new ContributionScanResult(findings
            .DistinctBy(item => item.Code, StringComparer.Ordinal)
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray());

        void AddIf(bool condition, string code, ContributionFindingSeverity severity)
        {
            if (condition)
            {
                findings.Add(new ContributionFinding(code, severity));
            }
        }
    }

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16})\b", RegexOptions.CultureInvariant)]
    private static partial Regex ApiCredentialRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SocialSecurityRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+?1[ .-]?)?\(?\d{3}\)?[ .-]\d{3}[ .-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"(?:powershell[^\r\n]{0,80}-enc(?:odedcommand)?|invoke-expression|curl[^\r\n]{0,120}\|\s*(?:sh|bash)|rm\s+-rf\s+/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaliciousCommandRegex();

    [GeneratedRegex(@"^(?:hello world|test|dummy|lorem ipsum)[.!\s]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DummyRegex();
}
