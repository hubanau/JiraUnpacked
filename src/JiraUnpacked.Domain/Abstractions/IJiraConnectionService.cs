namespace JiraUnpacked.Domain.Abstractions;

public sealed record ConnectionView(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);

public interface IJiraConnectionService
{
    Task<ConnectionView> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default);
    Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default);
    Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default);
}
