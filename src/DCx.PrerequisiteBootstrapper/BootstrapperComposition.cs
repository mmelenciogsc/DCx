using DCx.Core.Deployment;
using DCx.PrerequisiteBootstrapper.Core;

namespace DCx.PrerequisiteBootstrapper;

/// <summary>
/// Supplies evidence to the visual review surface. Neither composition detects machine state or
/// executes a launch plan. The fixture's evidence is fixed and synthetic for UI automation only.
/// </summary>
internal sealed class BootstrapperComposition
{
    private BootstrapperComposition(bool isFixture, IBootstrapperActivityRecorder recorder)
    {
        IsFixture = isFixture;
        Recorder = recorder;
    }

    public bool IsFixture { get; }

    public IBootstrapperActivityRecorder Recorder { get; }

    public static BootstrapperComposition CreateNormal() =>
        new(isFixture: false, new NoOpBootstrapperActivityRecorder());

    public static BootstrapperComposition CreateFixture() =>
        new(isFixture: true, new NoOpBootstrapperActivityRecorder());

    public PrerequisiteDetectionEvidence CreateDetectionEvidence(
        PrerequisiteBootstrapperSession session,
        string prerequisiteId,
        DateTimeOffset nowUtc) =>
        new(
            session.Id,
            prerequisiteId,
            IsFixture ? BootstrapperDetectionStatus.Missing : BootstrapperDetectionStatus.Unknown,
            // The fixture needs deterministic fresh evidence; normal composition has no detector.
            IsFixture ? nowUtc : nowUtc);

    public SelectedVendorArtifact? TrySelectPrevalidatedFixtureArtifact(
        PrerequisiteBootstrapperSession session,
        PrerequisiteBootstrapPrerequisite prerequisite,
        DateTimeOffset nowUtc)
    {
        if (!IsFixture || prerequisite.Id is not ("webview2-runtime" or "docker-desktop-windows"))
        {
            return null;
        }

        Recorder.Record("fixture_prevalidated_synthetic_artifact_selected");
        return new SelectedVendorArtifact(
            session.Id,
            prerequisite.Id,
            new string('f', 64),
            prerequisite.Publisher,
            prerequisite.DisplayName,
            "C:\\Fixture\\prevalidated-synthetic-installer.exe",
            nowUtc);
    }
}

/// <summary>Intentionally non-operational hook retained solely for fixture observation.</summary>
internal interface IBootstrapperActivityRecorder
{
    void Record(string activity);
}

/// <summary>Records no external action and never starts a process, browser, UAC prompt, or network request.</summary>
internal sealed class NoOpBootstrapperActivityRecorder : IBootstrapperActivityRecorder
{
    public void Record(string activity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activity);
        // Deliberately no-op: the fixture has no side effects or external adapters.
    }
}
