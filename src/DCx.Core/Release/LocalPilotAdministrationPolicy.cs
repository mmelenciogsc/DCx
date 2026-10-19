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

public static class LocalPilotAdministrationPolicy
{
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

    private static LocalPilotIncidentScenario Scenario(
        string id,
        string title,
        string affectedCapability,
        string stableIssueCode) =>
        new(id, title, affectedCapability, stableIssueCode);
}
