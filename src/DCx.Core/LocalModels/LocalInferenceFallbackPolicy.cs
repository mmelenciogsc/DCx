namespace DCx.Core.LocalModels;

public enum LocalInferenceRoute
{
    LocalGpu,
    LocalCpu,
    ApprovedRemote,
    Unavailable,
}

public sealed record LocalInferenceAvailability(
    bool LocalGpuAvailable,
    bool LocalCpuAvailable,
    bool InternetAvailable,
    bool RemoteProviderApproved,
    bool ContainsPrivateData);

public sealed record LocalInferenceDecision(LocalInferenceRoute Route, string Reason);

public static class LocalInferenceFallbackPolicy
{
    public static LocalInferenceDecision Decide(LocalInferenceAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);
        if (availability.LocalGpuAvailable)
        {
            return new LocalInferenceDecision(LocalInferenceRoute.LocalGpu, "Use the validated local GPU backend.");
        }

        if (availability.LocalCpuAvailable)
        {
            return new LocalInferenceDecision(LocalInferenceRoute.LocalCpu, "Use the bounded local CPU fallback.");
        }

        if (availability.InternetAvailable && availability.RemoteProviderApproved && !availability.ContainsPrivateData)
        {
            return new LocalInferenceDecision(
                LocalInferenceRoute.ApprovedRemote,
                "Use the separately approved remote provider for nonprivate input.");
        }

        return new LocalInferenceDecision(
            LocalInferenceRoute.Unavailable,
            availability.ContainsPrivateData
                ? "No local backend is available and private input cannot fall back remotely."
                : "No validated local backend or explicitly approved reachable remote provider is available.");
    }
}
