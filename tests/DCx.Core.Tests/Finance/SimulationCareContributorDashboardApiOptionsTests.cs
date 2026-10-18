using System.Security.Cryptography;
using System.Text;
using DCx.Core.Finance;
using DCx.GlobalService;

namespace DCx.Core.Tests.Finance;

public sealed class SimulationCareContributorDashboardApiOptionsTests
{
    [Fact]
    public void Parse_IsAbsentOnlyWhenEverySettingIsAbsent()
    {
        Assert.Null(SimulationCareContributorDashboardApiOptions.Parse(null, null, null));

        Assert.Throws<InvalidOperationException>(() =>
            SimulationCareContributorDashboardApiOptions.Parse(
                null,
                Digest("token"),
                "contributor:fixture"));
    }

    [Theory]
    [InlineData("0", "digest", "contributor:fixture")]
    [InlineData("1", "digest", "contributor:fixture")]
    [InlineData("1", "valid", "invalid contributor with spaces")]
    public void Parse_RejectsDisabledMalformedOrInvalidSubjectConfiguration(
        string enabled,
        string digestCase,
        string contributor)
    {
        var digest = digestCase == "valid" ? Digest("token") : digestCase;

        if (enabled != "1" || digestCase != "valid")
        {
            Assert.Throws<InvalidOperationException>(() =>
                SimulationCareContributorDashboardApiOptions.Parse(
                    enabled,
                    digest,
                    contributor));
        }
        else
        {
            Assert.Throws<PayPalSandboxException>(() =>
                SimulationCareContributorDashboardApiOptions.Parse(
                    enabled,
                    digest,
                    contributor));
        }
    }

    [Fact]
    public void Parse_StoresOnlyTokenDigestAndOneValidatedSubject()
    {
        const string token = "care-contributor-token-that-is-not-retained";
        const string contributor = "contributor:configured-fixture";

        var options = Assert.IsType<SimulationCareContributorDashboardApiOptions>(
            SimulationCareContributorDashboardApiOptions.Parse(
                "1",
                Digest(token),
                contributor));

        Assert.Equal(32, options.ContributorTokenSha256.Length);
        Assert.Equal(Digest(token), Convert.ToHexStringLower(options.ContributorTokenSha256));
        Assert.Equal(contributor, options.ContributorPseudonym);
        Assert.DoesNotContain(
            token,
            Convert.ToHexString(options.ContributorTokenSha256),
            StringComparison.Ordinal);
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
