using DCx.Core.Release;

namespace DCx.Core.Tests.Release;

public sealed class LocalPilotAdministrationPolicyTests
{
    [Fact]
    public void IncidentCatalog_IsClosedUniqueAndCoversRequiredExercises()
    {
        var scenarios = LocalPilotAdministrationPolicy.IncidentScenarios;

        Assert.Equal(7, scenarios.Count);
        Assert.Equal(7, scenarios.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, scenarios.Select(item => item.StableIssueCode).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(scenarios, item => item.Id == "signing-key-compromise");
        Assert.Contains(scenarios, item => item.Id == "malicious-global-update");
        Assert.Contains(scenarios, item => item.Id == "private-data-exposure");
        Assert.Contains(scenarios, item => item.Id == "inaccessible-takeover");
        Assert.Contains(scenarios, item => item.Id == "payout-reconciliation");
        Assert.Contains(scenarios, item => item.Id == "updater-rollback");
        Assert.Contains(scenarios, item => item.Id == "backup-restore");
        Assert.All(scenarios, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title));
            Assert.False(string.IsNullOrWhiteSpace(item.AffectedCapability));
            Assert.StartsWith("pilot-", item.StableIssueCode, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DryRun_ProducesMinimizedStopPlanWithoutAuthorityOrExternalActions()
    {
        var report = LocalPilotAdministrationPolicy.RunIncidentStopDryRun(
            CurrentReleaseReadiness.EvaluateLocalNoGo());

        Assert.True(report.Passed);
        Assert.Equal(7, report.ScenarioCount);
        Assert.Equal(0, report.ExternalActions);
        Assert.Equal(0, report.PilotEnrollmentControls);
        Assert.Equal(0, report.ProductEventSources);
        Assert.Equal(0, report.NetworkMetricTransports);
        Assert.Equal(0, report.ProductionActivationControls);
        Assert.True(report.RequiresFreshReadiness);
        Assert.Equal(7, report.Results.Count);
        Assert.All(report.Results, result =>
        {
            Assert.Contains("Preserve minimized evidence", result, StringComparison.Ordinal);
            Assert.Contains("fresh P14 readiness decision", result, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DryRun_RejectsAnApprovedProductionState()
    {
        var result = ReleaseReadinessEvaluator.Evaluate(
            [new ReleaseReadinessIssue(
                "validated",
                "Validated gate",
                ReadinessSeverity.Major,
                ReadinessDisposition.Validated,
                "Release engineering",
                "Retain validation.",
                "docs/progress/PHASE-14.md")],
            productionActivationApproved: true);

        Assert.Equal(ReleaseDecision.Go, result.Decision);
        Assert.Throws<InvalidOperationException>(() =>
            LocalPilotAdministrationPolicy.RunIncidentStopDryRun(result));
    }
}
