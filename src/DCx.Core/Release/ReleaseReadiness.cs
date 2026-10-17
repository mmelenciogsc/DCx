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
        Array.AsReadOnly<PilotMetricDefinition>(
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
        ]);

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

public static class CurrentReleaseReadiness
{
    public static IReadOnlyList<ReleaseReadinessIssue> Issues { get; } =
        Array.AsReadOnly<ReleaseReadinessIssue>(
        [
            Issue("p06-local-models", "Local transcription and embeddings", ReadinessSeverity.Major,
                "Local-model engineering", "With owner approval, install and benchmark a suitable Whisper backend and embedding model.", "docs/progress/PHASE-06.md"),
            Issue("p09-compose", "Authoritative global-service container simulation", ReadinessSeverity.Major,
                "Platform engineering", "Provide an approved native Docker/Compose environment and rerun the two-client gate.", "docs/progress/PHASE-09.md"),
            Issue("p10-ccb-sandbox", "Production hostile community-code isolation", ReadinessSeverity.Critical,
                "Security engineering", "Deploy the gated package pipeline only to an approved Ubuntu 24.04 environment with seccomp, cgroup-v2, immutable-image, authenticated-review and external penetration-test evidence before enabling CCB publication.", "docs/progress/PHASE-10.md"),
            Issue("p11-paypal", "PayPal sandbox eligibility and reconciliation", ReadinessSeverity.Critical,
                "Finance platform and owner", "Provide approved sandbox credentials and merchant/payout eligibility evidence; implement subscription mapping plus accessible recipient-snapshot preparation/approval, then run official-adapter acceptance and post-terminal payout-reversal reconciliation.", "docs/progress/PHASE-11.md"),
            Issue("p12-live-acceptance", "Live provider and human acceptance scenarios", ReadinessSeverity.Major,
                "Quality and accessibility", "Run separately authorized Facebook, QuickBooks, YouTube, installer and assistive-technology acceptance.", "docs/progress/PHASE-12.md"),
            Issue("p13-signed-installer", "Signed installer and clean-VM lifecycle", ReadinessSeverity.Critical,
                "Release and security engineering", "Build a signed MSIX and pass install, upgrade, rollback, uninstall, AV and vulnerability checks.", "docs/progress/PHASE-13.md"),
            Issue("p14-policy-approvals", "Privacy, terms, CARE and provider distribution approvals", ReadinessSeverity.Critical,
                "Owner, legal and compliance", "Approve final privacy, terms, commission, provider and pilot policies before activation.", "docs/progress/PHASE-14.md"),
        ]);

    public static ReleaseReadinessResult EvaluateLocalNoGo() =>
        ReleaseReadinessEvaluator.Evaluate(Issues, productionActivationApproved: false);

    private static ReleaseReadinessIssue Issue(
        string id,
        string title,
        ReadinessSeverity severity,
        string owner,
        string nextAction,
        string evidence) =>
        new(id, title, severity, ReadinessDisposition.Blocked, owner, nextAction, evidence);
}
