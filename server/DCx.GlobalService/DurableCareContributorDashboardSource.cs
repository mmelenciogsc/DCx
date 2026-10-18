using DCx.Core.Finance;

namespace DCx.GlobalService;

public sealed class DurableCareContributorDashboardSource(
    DurableCareRepository repository,
    string authenticatedContributorPseudonym,
    Func<DateTimeOffset>? utcNow = null) : ICareContributorDashboardSource
{
    private readonly Func<DateTimeOffset> clock = utcNow ?? (() => DateTimeOffset.UtcNow);

    public Task<CareContributorDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default) =>
        repository.GetContributorDashboardAsync(
            authenticatedContributorPseudonym,
            clock(),
            cancellationToken);
}
