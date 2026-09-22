using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record ConnectionView(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

public enum CreateConnectionStatus { Created, InvalidCredentials, JiraUnreachable, InvalidBaseUrl }

/// <param name="Error">Why the base URL was refused; set only for <see cref="CreateConnectionStatus.InvalidBaseUrl"/>.</param>
public sealed record CreateConnectionResult(CreateConnectionStatus Status, ConnectionView? View, string? Error = null);

public enum UpdateConnectionStatus { Updated, NotFound, InvalidCredentials, JiraUnreachable, InvalidBaseUrl }

/// <param name="Error">Why the stored base URL was refused; set only for <see cref="UpdateConnectionStatus.InvalidBaseUrl"/>.</param>
public sealed record UpdateConnectionResult(UpdateConnectionStatus Status, ConnectionView? View, string? Error = null);

public interface IJiraConnectionService
{
    Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default);
    Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken ct = default);
    Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the stored API token (and optionally the email). The new pair is validated against
    /// Jira first; nothing is written unless Jira accepts it.
    /// </summary>
    Task<UpdateConnectionResult> UpdateCredentialsAsync(Guid id, string? email, string apiToken, CancellationToken ct = default);

    /// <summary>
    /// Deletes the connection, every project under it, all of their data, and the connection's
    /// dictionaries. Refused while any of its projects has a Running sync.
    /// </summary>
    Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken ct = default);
}
