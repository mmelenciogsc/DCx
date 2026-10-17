using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Release;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DCx.ReadinessReport <output-json>");
    return 2;
}

ReleaseReadinessIssue[] issues =
[
    Issue("p06-local-models", "Local transcription and embeddings", ReadinessSeverity.Major,
        "Local-model engineering", "With owner approval, install and benchmark a suitable Whisper backend and embedding model.", "docs/progress/PHASE-06.md"),
    Issue("p09-compose", "Authoritative global-service container simulation", ReadinessSeverity.Major,
        "Platform engineering", "Provide an approved native Docker/Compose environment and rerun the two-client gate.", "docs/progress/PHASE-09.md"),
    Issue("p10-ccb-sandbox", "Hostile community-code isolation", ReadinessSeverity.Critical,
        "Security engineering", "Implement and penetration-test a real untrusted-code sandbox before enabling CCB publication.", "docs/progress/PHASE-10.md"),
    Issue("p11-paypal", "PayPal sandbox eligibility and reconciliation", ReadinessSeverity.Critical,
        "Finance platform and owner", "Provide approved sandbox credentials and eligibility evidence; implement official verification and reconciliation.", "docs/progress/PHASE-11.md"),
    Issue("p12-live-acceptance", "Live provider and human acceptance scenarios", ReadinessSeverity.Major,
        "Quality and accessibility", "Run separately authorized Facebook, QuickBooks, YouTube, installer and assistive-technology acceptance.", "docs/progress/PHASE-12.md"),
    Issue("p13-signed-installer", "Signed installer and clean-VM lifecycle", ReadinessSeverity.Critical,
        "Release and security engineering", "Build a signed MSIX and pass install, upgrade, rollback, uninstall, AV and vulnerability checks.", "docs/progress/PHASE-13.md"),
    Issue("p14-policy-approvals", "Privacy, terms, CARE and provider distribution approvals", ReadinessSeverity.Critical,
        "Owner, legal and compliance", "Approve final privacy, terms, commission, provider and pilot policies before activation.", "docs/progress/PHASE-14.md"),
];

var assessment = ReleaseReadinessEvaluator.Evaluate(issues, productionActivationApproved: false);
var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var temporary = $"{output}.{Guid.NewGuid():N}.tmp";
try
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    var json = JsonSerializer.Serialize(assessment, options);
    await File.WriteAllTextAsync(temporary, json);
    File.Move(temporary, output, overwrite: true);
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}

Console.WriteLine($"decision={(assessment.Decision == ReleaseDecision.NoGo ? "NO_GO" : "GO")}");
Console.WriteLine($"critical_and_major_issues={assessment.CriticalAndMajorIssues}");
Console.WriteLine($"critical_and_major_triaged={assessment.CriticalAndMajorIssuesTriaged}");
Console.WriteLine($"blocking_critical_and_major={assessment.BlockingCriticalAndMajorIssues}");
Console.WriteLine($"pilot_metrics={assessment.PilotMetrics.Count}");
Console.WriteLine($"production_activation_approved={assessment.ProductionActivationApproved}");
Console.WriteLine($"report={output}");
return assessment.Decision == ReleaseDecision.NoGo && !assessment.ProductionActivationApproved ? 0 : 1;

static ReleaseReadinessIssue Issue(
    string id,
    string title,
    ReadinessSeverity severity,
    string owner,
    string nextAction,
    string evidence) =>
    new(id, title, severity, ReadinessDisposition.Blocked, owner, nextAction, evidence);
