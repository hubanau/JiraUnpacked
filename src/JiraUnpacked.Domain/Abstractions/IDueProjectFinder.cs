namespace JiraUnpacked.Domain.Abstractions;

public sealed record DueProject(Guid ProjectId, Guid EndUserId, int SyncIntervalMinutes);

public interface IDueProjectFinder
{
    Task<IReadOnlyList<DueProject>> GetDueProjectsAsync(CancellationToken ct = default);
}
