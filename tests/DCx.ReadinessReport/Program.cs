using System.Text.Json;
using System.Text.Json.Serialization;
using DCx.Core.Release;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: DCx.ReadinessReport <output-json>");
    return 2;
}

var assessment = CurrentReleaseReadiness.EvaluateLocalNoGo();
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
