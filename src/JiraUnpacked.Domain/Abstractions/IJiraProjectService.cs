namespace JiraUnpacked.Domain.Abstractions;

public enum AddProjectStatus { Created, ConnectionNotFound, UnknownProjectKey, Duplicate, InvalidInterval, JiraUnreachable }
public sealed record ProjectView(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);
public sealed record AddProjectResult(AddProjectStatus Status, ProjectView? View);

public interface IJiraProjectService
{
    Task<AddProjectResult> AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectView>> ListAsync(Guid connectionId, CancellationToken ct = default);
    Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> UpdateIntervalAsync(Guid projectId, int syncIntervalMinutes, CancellationToken ct = default); // null = not found, false = invalid interval, true = updated
}
