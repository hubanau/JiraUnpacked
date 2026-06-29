namespace JiraUnpacked.Domain.Abstractions;

public enum AddQueryStatus { Created, ProjectNotFound, InvalidJql, JiraUnreachable }
public sealed record QueryView(Guid Id, Guid JiraProjectId, string Jql, DateTime CreatedAtUtc);
public sealed record AddQueryResult(AddQueryStatus Status, QueryView? View, IReadOnlyList<string> Errors);

public interface IJiraQueryService
{
    Task<AddQueryResult> AddAsync(Guid projectId, string jql, CancellationToken ct = default);
    Task<IReadOnlyList<QueryView>> ListAsync(Guid projectId, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid queryId, CancellationToken ct = default);
}
