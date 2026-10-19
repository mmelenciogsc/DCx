namespace DCx.Core.Release;

public sealed record LocalPilotIncidentScenario(
    string Id,
    string Title,
    string AffectedCapability,
    string StableIssueCode);

public sealed record LocalPilotIncidentExerciseReport(
    bool Passed,
    int ScenarioCount,
    int ExternalActions,
    int PilotEnrollmentControls,
    int ProductEventSources,
    int NetworkMetricTransports,
    int ProductionActivationControls,
    bool RequiresFreshReadiness,
    IReadOnlyList<string> Results);

public sealed record LocalPilotCohortStage(
    string Id,
    string Title,
    string Audience,
    string EntryGate,
    string ExitGate);

public sealed record LocalPilotCohortPlanPreview(
    string State,
    int StageCount,
    int PilotEnrollmentControls,
    int ProductEventSources,
    int NetworkMetricTransports,
    int ProductionActivationControls,
    bool OwnerApprovalRecorded,
    bool CohortSizeAndScheduleApproved,
    IReadOnlyList<LocalPilotCohortStage> Stages);

public static class LocalPilotAdministrationPolicy
{
    public static IReadOnlyList<LocalPilotCohortStage> CohortStages { get; } =
        Array.AsReadOnly<LocalPilotCohortStage>(
        [
            Cohort(
                "owner-rehearsal",
                "Owner-supervised local rehearsal",
                "Named internal testers using generated or explicitly approved data",
                "Close every critical and major blocker; approve privacy, support, incident and provider policies; authorize the exact cohort.",
                "Review all aggregate evidence and drills; leave no unresolved major issue before expansion."),
            Cohort(
                "assistive-technology",
                "Assistive-technology cohort",
                "Named keyboard, JAWS, NVDA and Narrator participants",
                "Approve the rehearsal exit; complete human assistive-technology acceptance; staff accessible support and stop channels.",
                "Triage every accessibility failure and verify consent withdrawal before expansion."),
            Cohort(
                "limited-opt-in",
                "Limited opt-in cohort",
                "A bounded, explicitly consenting participant group",
                "Approve the accessibility exit; approve cohort size, duration, retention, support and incident thresholds.",
                "Reconcile reliability, privacy, cost, sync and CARE evidence with no unresolved major issue."),
            Cohort(
                "expanded-release-candidate",
                "Expanded release-candidate cohort",
                "A separately approved bounded release-candidate group",
                "Approve the limited-cohort exit and obtain a fresh owner GO decision with every provider and distribution gate closed.",
                "Complete final release review; production activation remains a separate explicit decision."),
        ]);

    public static IReadOnlyList<LocalPilotIncidentScenario> IncidentScenarios { get; } =
        Array.AsReadOnly<LocalPilotIncidentScenario>(
        [
            Scenario("signing-key-compromise", "Signing-key compromise", "package signing and update", "pilot-signing-key"),
            Scenario("malicious-global-update", "Malicious global update", "global knowledge synchronization", "pilot-global-update"),
            Scenario("private-data-exposure", "Private-data exposure", "affected private-data processing", "pilot-private-data"),
            Scenario("inaccessible-takeover", "Inaccessible takeover", "agent and user shared control", "pilot-accessibility-control"),
            Scenario("payout-reconciliation", "Payout duplicate or reconciliation variance", "CARE payout processing", "pilot-care-reconciliation"),
            Scenario("updater-rollback", "Updater rollback", "controlled package update", "pilot-updater-rollback"),
            Scenario("backup-restore", "Backup and restore failure", "local backup and recovery", "pilot-backup-restore"),
        ]);

    public static LocalPilotIncidentExerciseReport RunIncidentStopDryRun(
        ReleaseReadinessResult readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (readiness.Decision != ReleaseDecision.NoGo || readiness.ProductionActivationApproved)
        {
            throw new InvalidOperationException(
                "The local incident exercise is available only while production activation is denied.");
        }

        var results = IncidentScenarios
            .Select(scenario =>
                $"{scenario.Title}. Stop {scenario.AffectedCapability}. Preserve minimized evidence under {scenario.StableIssueCode}. Require explicit closure and a fresh P14 readiness decision before resumption.")
            .ToArray();

        return new LocalPilotIncidentExerciseReport(
            Passed: results.Length == IncidentScenarios.Count,
            ScenarioCount: results.Length,
            ExternalActions: 0,
            PilotEnrollmentControls: 0,
            ProductEventSources: 0,
            NetworkMetricTransports: 0,
            ProductionActivationControls: 0,
            RequiresFreshReadiness: true,
            Results: results);
    }

    public static LocalPilotCohortPlanPreview ReviewCohortPlan(
        ReleaseReadinessResult readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        if (readiness.Decision != ReleaseDecision.NoGo || readiness.ProductionActivationApproved ||
            readiness.BlockingCriticalAndMajorIssues <= 0 ||
            readiness.CriticalAndMajorIssuesTriaged != readiness.CriticalAndMajorIssues)
        {
            throw new InvalidOperationException(
                "The zero-authority cohort preview requires a fully triaged blocked NO_GO state.");
        }

        return new LocalPilotCohortPlanPreview(
            State: "blocked-not-enrolled",
            StageCount: CohortStages.Count,
            PilotEnrollmentControls: 0,
            ProductEventSources: 0,
            NetworkMetricTransports: 0,
            ProductionActivationControls: 0,
            OwnerApprovalRecorded: false,
            CohortSizeAndScheduleApproved: false,
            Stages: CohortStages);
    }

    private static LocalPilotIncidentScenario Scenario(
        string id,
        string title,
        string affectedCapability,
        string stableIssueCode) =>
        new(id, title, affectedCapability, stableIssueCode);

    private static LocalPilotCohortStage Cohort(
        string id,
        string title,
        string audience,
        string entryGate,
        string exitGate) =>
        new(id, title, audience, entryGate, exitGate);
}
