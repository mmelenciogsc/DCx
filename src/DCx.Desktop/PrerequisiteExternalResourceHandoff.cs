using System.Diagnostics;
using DCx.Core.Deployment;

namespace DCx.Desktop;

/// <summary>
/// Opens a policy-approved official prerequisite resource. Implementations must not download,
/// execute installers or package managers, request elevation, or invoke PowerShell.
/// </summary>
public interface IPrerequisiteExternalResourceHandoff
{
    PrerequisiteExternalResourceHandoffResult OpenOfficialResource(
        PrerequisiteExternalResourceHandoffDecision decision);
}

public sealed record PrerequisiteExternalResourceHandoffResult(string StatusText);

internal sealed class WindowsPrerequisiteExternalResourceHandoff : IPrerequisiteExternalResourceHandoff
{
    public PrerequisiteExternalResourceHandoffResult OpenOfficialResource(
        PrerequisiteExternalResourceHandoffDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Permitted || decision.OfficialResourceUri is null)
        {
            throw new InvalidOperationException("The official prerequisite resource handoff is not permitted.");
        }

        var startInfo = new ProcessStartInfo(decision.OfficialResourceUri.AbsoluteUri)
        {
            UseShellExecute = true,
            Verb = string.Empty,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The configured browser did not accept the reviewed official resource.");

        return new PrerequisiteExternalResourceHandoffResult(
            "The exact reviewed official resource was opened in your configured browser.");
    }
}

/// <summary>
/// Fixture-only adapter that records a permitted handoff without starting an external process.
/// </summary>
internal sealed class RecordingFixturePrerequisiteExternalResourceHandoff : IPrerequisiteExternalResourceHandoff
{
    private readonly List<Uri> recordedResources = [];

    public IReadOnlyList<Uri> RecordedResources => recordedResources;

    public PrerequisiteExternalResourceHandoffResult OpenOfficialResource(
        PrerequisiteExternalResourceHandoffDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Permitted || decision.OfficialResourceUri is null)
        {
            throw new InvalidOperationException("The fixture official prerequisite resource handoff is not permitted.");
        }

        recordedResources.Add(decision.OfficialResourceUri);
        return new PrerequisiteExternalResourceHandoffResult(
            "Fixture recorded the exact reviewed official-resource handoff without opening an external application.");
    }
}
