using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Jira;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraConnectionService : IJiraConnectionService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IJiraApiClient _jira;
    private readonly ITokenProtector _protector;
    private readonly JiraBaseUrlValidator _baseUrls;

    public JiraConnectionService(AppDbContext db, IClock clock, IJiraApiClient jira, ITokenProtector protector,
        JiraBaseUrlValidator baseUrls)
    {
        _db = db; _clock = clock; _jira = jira; _protector = protector; _baseUrls = baseUrls;
    }

    public async Task<CreateConnectionResult> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        // Before anything is sent: the credentials must never reach a host the policy refuses.
        var check = _baseUrls.Check(baseUrl);
        if (!check.IsValid)
            return new CreateConnectionResult(CreateConnectionStatus.InvalidBaseUrl, null, check.Error);
        baseUrl = check.NormalizedUrl!;

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

    public async Task<UpdateConnectionResult> UpdateCredentialsAsync(
        Guid id, string? email, string apiToken, CancellationToken ct = default)
    {
        var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return new UpdateConnectionResult(UpdateConnectionStatus.NotFound, null);

        // A connection stored before the base-URL policy existed may name a host it now refuses.
        // Sending it a fresh token would be exactly what the policy exists to prevent.
        var check = _baseUrls.Check(c.BaseUrl);
        if (!check.IsValid)
            return new UpdateConnectionResult(UpdateConnectionStatus.InvalidBaseUrl, null, check.Error);

        var newEmail = string.IsNullOrWhiteSpace(email) ? c.Email : email.Trim();

        // Validate first and write only on success, so a mistyped token can never replace one
        // that works.
        var validation = await _jira.ValidateAsync(new JiraCredentials(c.BaseUrl, newEmail, apiToken), ct);
        if (validation == JiraValidationResult.InvalidCredentials)
            return new UpdateConnectionResult(UpdateConnectionStatus.InvalidCredentials, null);
        if (validation == JiraValidationResult.Unreachable)
            return new UpdateConnectionResult(UpdateConnectionStatus.JiraUnreachable, null);

        c.Email = newEmail;
        c.ApiTokenEncrypted = _protector.Protect(apiToken);
        await _db.SaveChangesAsync(ct);
        return new UpdateConnectionResult(UpdateConnectionStatus.Updated, ToView(c));
    }

    public async Task<DeleteOutcome> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Tenant-filtered: another tenant's connection id is simply not found.
        if (!await _db.JiraConnections.AnyAsync(c => c.Id == id, ct)) return DeleteOutcome.NotFound;

        // Lock the connection first so no project can be added under it meanwhile (adding one
        // needs a key-share lock on this row), then its projects so no sync can start.
        await _db.Database
            .SqlQuery<Guid>($@"SELECT ""Id"" AS ""Value"" FROM ""JiraConnections"" WHERE ""Id"" = {id} FOR UPDATE")
            .ToListAsync(ct);
        var projectIds = await _db.JiraProjects.Where(p => p.JiraConnectionId == id).Select(p => p.Id).ToArrayAsync(ct);
        await ProjectDataEraser.LockProjectsAsync(_db, projectIds, ct);
        if (await ProjectDataEraser.AnyRunningAsync(_db, projectIds, ct)) return DeleteOutcome.SyncRunning;

        await ProjectDataEraser.DeleteProjectsAsync(_db, projectIds, ct);

        // The connection-scoped dictionaries. Only now: issues referenced them (NO ACTION).
        await _db.Statuses.Where(x => x.JiraConnectionId == id).ExecuteDeleteAsync(ct);
        await _db.IssueTypes.Where(x => x.JiraConnectionId == id).ExecuteDeleteAsync(ct);
        await _db.Priorities.Where(x => x.JiraConnectionId == id).ExecuteDeleteAsync(ct);
        await _db.JiraUsers.Where(x => x.JiraConnectionId == id).ExecuteDeleteAsync(ct);
        await _db.JiraConnections.Where(x => x.Id == id).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return DeleteOutcome.Deleted;
    }

    // Loads a tenant-scoped connection and decrypts its token; null if not found.
    private async Task<JiraCredentials?> CredsAsync(Guid connectionId, CancellationToken ct)
    {
        var c = await _db.JiraConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
        return c is null ? null : new JiraCredentials(c.BaseUrl, c.Email, _protector.Unprotect(c.ApiTokenEncrypted));
    }

    private static ConnectionView ToView(JiraConnection c) => new(c.Id, c.BaseUrl, c.Email, c.CreatedAt);
}
