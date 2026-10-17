namespace DCx.Core.Release;

public enum ReadinessDisposition
{
    Validated,
    Blocked,
    Deferred,
}

public enum ReadinessSeverity
{
    Critical,
    Major,
    Minor,
}

public enum ReleaseDecision
{
    Go,
    NoGo,
}

public sealed record ReleaseReadinessIssue(
    string Id,
    string Title,
    ReadinessSeverity Severity,
    ReadinessDisposition Disposition,
    string OwnerRole,
    string NextAction,
    string EvidenceReference);

public sealed record PilotMetricDefinition(
    string Key,
    string Unit,
    string Aggregation,
    string PrivacyRule);

public sealed record ReleaseReadinessResult(
    ReleaseDecision Decision,
    bool ProductionActivationApproved,
    int CriticalAndMajorIssues,
    int CriticalAndMajorIssuesTriaged,
    int BlockingCriticalAndMajorIssues,
    IReadOnlyList<ReleaseReadinessIssue> Issues,
    IReadOnlyList<PilotMetricDefinition> PilotMetrics);

public static class PilotMetricCatalog
{
    public static IReadOnlyList<PilotMetricDefinition> All { get; } =
    [
        Metric("task_completion_rate", "ratio", "aggregate", "No prompt, output, URL, path or user identifier."),
        Metric("task_duration_ms", "milliseconds", "count, minimum, maximum and percentile", "Timing only."),
        Metric("model_input_tokens", "tokens", "sum by approved route class", "No prompt or account identifier."),
        Metric("model_output_tokens", "tokens", "sum by approved route class", "No response body."),
        Metric("tested_ccb_reuse_ratio", "ratio", "aggregate", "Capability class only; no artifact body."),
        Metric("retrieval_success_rate", "ratio", "aggregate", "No query, result body or local path."),
        Metric("crash_count", "count", "aggregate by stable error code", "No dump or private exception text by default."),
        Metric("user_control_revocation_count", "count", "aggregate", "No keystroke or field value."),
        Metric("false_agent_claim_count", "count", "aggregate by reviewed issue code", "No prompt or model output body."),
        Metric("accessibility_failure_count", "count", "aggregate by control and issue code", "No screen content."),
        Metric("global_sync_bytes", "bytes", "sum", "No private-store or contribution content."),
        Metric("care_reconciliation_variance_cents", "US-dollar cents", "sum and maximum absolute variance", "No PayPal or contributor account identifier."),
    ];

    private static PilotMetricDefinition Metric(string key, string unit, string aggregation, string privacyRule) =>
        new(key, unit, aggregation, privacyRule);
}

public static class ReleaseReadinessEvaluator
{
    public static ReleaseReadinessResult Evaluate(
        IReadOnlyList<ReleaseReadinessIssue> issues,
        bool productionActivationApproved)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0) throw new ArgumentException("At least one readiness issue or validated gate is required.", nameof(issues));

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (string.IsNullOrWhiteSpace(issue.Id) || !identifiers.Add(issue.Id) ||
                string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(issue.OwnerRole) ||
                string.IsNullOrWhiteSpace(issue.NextAction) || string.IsNullOrWhiteSpace(issue.EvidenceReference))
            {
                throw new ArgumentException("Every readiness issue requires a unique ID, owner, next action and evidence reference.", nameof(issues));
            }
        }

        var criticalAndMajor = issues
            .Where(issue => issue.Severity is ReadinessSeverity.Critical or ReadinessSeverity.Major)
            .ToArray();
        var triaged = criticalAndMajor.Count(issue =>
            !string.IsNullOrWhiteSpace(issue.OwnerRole) &&
            !string.IsNullOrWhiteSpace(issue.NextAction) &&
            !string.IsNullOrWhiteSpace(issue.EvidenceReference));
        var blocking = criticalAndMajor.Count(issue => issue.Disposition != ReadinessDisposition.Validated);
        var decision = productionActivationApproved && triaged == criticalAndMajor.Length && blocking == 0
            ? ReleaseDecision.Go
            : ReleaseDecision.NoGo;

        return new ReleaseReadinessResult(
            decision,
            productionActivationApproved,
            criticalAndMajor.Length,
            triaged,
            blocking,
            issues.ToArray(),
            PilotMetricCatalog.All);
    }
}
