using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record ConnectionView(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

public enum CreateConnectionStatus { Created, InvalidCredentials, JiraUnreachable }
public sealed record CreateConnectionResult(CreateConnectionStatus Status, ConnectionView? View);

public interface IJiraConnectionService
{
    Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default);
    Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken ct = default);
}
