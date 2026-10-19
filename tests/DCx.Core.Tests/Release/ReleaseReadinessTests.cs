using DCx.Core.Release;

namespace DCx.Core.Tests.Release;

public sealed class ReleaseReadinessTests
{
    [Fact]
    public void BlockedMajorIssue_ForcesNoGoEvenWhenActivationIsApproved()
    {
        var result = ReleaseReadinessEvaluator.Evaluate(
            [Issue(ReadinessDisposition.Blocked)],
            productionActivationApproved: true);

        Assert.Equal(ReleaseDecision.NoGo, result.Decision);
        Assert.Equal(1, result.CriticalAndMajorIssuesTriaged);
        Assert.Equal(1, result.BlockingCriticalAndMajorIssues);
    }

    [Fact]
    public void ValidatedIssues_StillRequireExplicitProductionApproval()
    {
        var issues = new[] { Issue(ReadinessDisposition.Validated) };

        Assert.Equal(ReleaseDecision.NoGo, ReleaseReadinessEvaluator.Evaluate(issues, false).Decision);
        Assert.Equal(ReleaseDecision.Go, ReleaseReadinessEvaluator.Evaluate(issues, true).Decision);
    }

    [Fact]
    public void MissingTriageOrDuplicateIdentifier_IsRejected()
    {
        var missingOwner = Issue(ReadinessDisposition.Blocked) with { OwnerRole = string.Empty };
        Assert.Throws<ArgumentException>(() => ReleaseReadinessEvaluator.Evaluate([missingOwner], false));
        Assert.Throws<ArgumentException>(() => ReleaseReadinessEvaluator.Evaluate(
            [Issue(ReadinessDisposition.Validated), Issue(ReadinessDisposition.Validated)],
            false));
    }

    [Fact]
    public void PilotMetrics_AreCompleteUniqueAndContentMinimized()
    {
        Assert.Equal(12, PilotMetricCatalog.All.Count);
        Assert.Equal(12, PilotMetricCatalog.All.Select(metric => metric.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(PilotMetricCatalog.All, metric => metric.Key == "false_agent_claim_count");
        Assert.Contains(PilotMetricCatalog.All, metric => metric.Key == "care_reconciliation_variance_cents");
        Assert.All(PilotMetricCatalog.All, metric =>
        {
            Assert.DoesNotContain("prompt body", metric.PrivacyRule, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(metric.PrivacyRule));
        });
    }

    [Fact]
    public void CurrentReadinessPolicy_IsSharedTriagedAndNoGo()
    {
        var result = CurrentReleaseReadiness.EvaluateLocalNoGo();

        Assert.Equal(ReleaseDecision.NoGo, result.Decision);
        Assert.False(result.ProductionActivationApproved);
        Assert.Equal(7, result.Issues.Count);
        Assert.Equal(7, result.CriticalAndMajorIssuesTriaged);
        Assert.Equal(7, result.BlockingCriticalAndMajorIssues);
        Assert.Contains(result.Issues, issue => issue.Id == "p10-ccb-sandbox");
        var p12 = Assert.Single(result.Issues, issue => issue.Id == "p12-live-acceptance");
        Assert.Contains("URL-to-fillable-or-flattened-PDF", p12.NextAction, StringComparison.Ordinal);
        Assert.Contains("QuickBooks sandbox OAuth/API retrieval", p12.NextAction, StringComparison.Ordinal);
        var paypal = Assert.Single(result.Issues, issue => issue.Id == "p11-paypal");
        Assert.Contains("blocked-event closure/requeue", paypal.NextAction, StringComparison.Ordinal);
        Assert.Contains("external notification and escalation", paypal.NextAction, StringComparison.Ordinal);
    }

    private static ReleaseReadinessIssue Issue(ReadinessDisposition disposition) => new(
        "release-signing",
        "Signed installer lifecycle",
        ReadinessSeverity.Critical,
        disposition,
        "Release engineering",
        "Build and validate a signed package on a clean Windows 11 VM.",
        "docs/progress/PHASE-13.md");
}
