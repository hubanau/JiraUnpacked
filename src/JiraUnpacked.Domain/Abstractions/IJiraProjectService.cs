namespace JiraUnpacked.Domain.Abstractions;

public enum AddProjectStatus { Created, ConnectionNotFound, UnknownProjectKey, Duplicate, InvalidInterval }
public sealed record ProjectView(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, bool SyncPaused, DateTime CreatedAtUtc);
public sealed record AddProjectResult(AddProjectStatus Status, ProjectView? View);

public enum UpdateProjectStatus { Updated, NotFound, InvalidInterval, NothingToUpdate }

public interface IJiraProjectService
{
    Task<AddProjectResult> AddAsync(Guid connectionId, string projectKey, int syncIntervalMinutes, CancellationToken ct = default);
    Task<IReadOnlyList<ProjectView>> ListAsync(Guid connectionId, CancellationToken ct = default);
    Task<ProjectView?> GetAsync(Guid projectId, CancellationToken ct = default);

    /// <summary>
    /// Changes the interval, the scheduled-sync pause, or both. A null argument leaves that
    /// setting alone; both null is <see cref="UpdateProjectStatus.NothingToUpdate"/>.
    /// </summary>
    Task<UpdateProjectStatus> UpdateAsync(Guid projectId, int? syncIntervalMinutes, bool? syncPaused, CancellationToken ct = default);

    /// <summary>
    /// Deletes the project and everything derived from it. The connection and its dictionaries
    /// stay. Refused while the project has a Running sync.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(Guid projectId, CancellationToken ct = default);
}
