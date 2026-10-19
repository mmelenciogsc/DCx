using DCx.Core.Release;

namespace DCx.Core.Tests.Release;

public sealed class LocalPilotAdministrationPolicyTests
{
    [Fact]
    public void CohortCatalog_IsClosedOrderedAndRequiresFreshStageApproval()
    {
        var stages = LocalPilotAdministrationPolicy.CohortStages;

        Assert.Equal(4, stages.Count);
        Assert.Equal(
            ["owner-rehearsal", "assistive-technology", "limited-opt-in", "expanded-release-candidate"],
            stages.Select(stage => stage.Id));
        Assert.Equal(4, stages.Select(stage => stage.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(stages, stage =>
        {
            Assert.False(string.IsNullOrWhiteSpace(stage.Title));
            Assert.False(string.IsNullOrWhiteSpace(stage.Audience));
            Assert.Contains("approve", stage.EntryGate, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(stage.ExitGate));
        });
        Assert.Contains("production activation remains a separate explicit decision",
            stages[^1].ExitGate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CohortPreview_PreservesBlockedNotEnrolledZeroAuthorityState()
    {
        var preview = LocalPilotAdministrationPolicy.ReviewCohortPlan(
            CurrentReleaseReadiness.EvaluateLocalNoGo());

        Assert.Equal("blocked-not-enrolled", preview.State);
        Assert.Equal(4, preview.StageCount);
        Assert.Equal(0, preview.PilotEnrollmentControls);
        Assert.Equal(0, preview.ProductEventSources);
        Assert.Equal(0, preview.NetworkMetricTransports);
        Assert.Equal(0, preview.ProductionActivationControls);
        Assert.False(preview.OwnerApprovalRecorded);
        Assert.False(preview.CohortSizeAndScheduleApproved);
        Assert.Same(LocalPilotAdministrationPolicy.CohortStages, preview.Stages);
    }

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
        Assert.Throws<InvalidOperationException>(() =>
            LocalPilotAdministrationPolicy.ReviewCohortPlan(result));
    }
}
