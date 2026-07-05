namespace JiraUnpacked.Domain.Abstractions;

public interface ISyncRunQuery
{
    Task<IReadOnlyList<SyncRunView>> ListAsync(Guid projectId, CancellationToken ct = default);
}
