using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraConnectionService : IJiraConnectionService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;

    public JiraConnectionService(AppDbContext db, IClock clock, IJiraApiClient jira, ITokenProtector protector)
    {
        _db = db; _clock = clock; _jira = jira; _protector = protector;
    }

    public async Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        var validation = await _jira.ValidateAsync(new JiraCredentials(baseUrl, email, apiToken), ct);
        if (validation == JiraValidationResult.InvalidCredentials)
            return new CreateConnectionResult(CreateConnectionStatus.InvalidCredentials, null);
        if (validation == JiraValidationResult.Unreachable)
            return new CreateConnectionResult(CreateConnectionStatus.JiraUnreachable, null);

        var connection = new JiraConnection
        {
            Id = Guid.NewGuid(),
            BaseUrl = baseUrl,
            Email = email,
            ApiTokenEncrypted = _protector.Protect(apiToken),
            CreatedAt = _clock.UtcNow
        };
        _db.JiraConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return new CreateConnectionResult(CreateConnectionStatus.Created, ToView(connection));
    }

    public async Task<IReadOnlyList<ConnectionView>> ListAsync(CancellationToken ct = default) =>
        await _db.JiraConnections
            .OrderBy(c => c.CreatedAt)
            .Select(c => new ConnectionView(c.Id, c.BaseUrl, c.Email, c.CreatedAt))
            .ToListAsync(ct);

    public async Task<ConnectionView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null ? null : ToView(c);
    }

    public async Task<IReadOnlyList<JiraField>?> GetFieldsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var creds = await CredsAsync(connectionId, ct);
        return creds is null ? null : await _jira.GetFieldsAsync(creds, ct);
    }

    public async Task<IReadOnlyList<JiraProjectSummary>?> GetAvailableProjectsAsync(Guid connectionId, CancellationToken ct = default)
    {
        var creds = await CredsAsync(connectionId, ct);
        return creds is null ? null : await _jira.GetProjectsAsync(creds, ct);
    }

    // Loads a tenant-scoped connection and decrypts its token; null if not found.
    private async Task<JiraCredentials?> CredsAsync(Guid connectionId, CancellationToken ct)
    {
        var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
        return c is null ? null : new JiraCredentials(c.BaseUrl, c.Email, _protector.Unprotect(c.ApiTokenEncrypted));
    }

    private static ConnectionView ToView(JiraConnection c) => new(c.Id, c.BaseUrl, c.Email, c.CreatedAt);
}
