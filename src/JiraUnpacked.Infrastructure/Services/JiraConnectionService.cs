using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class JiraConnectionService : IJiraConnectionService
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public JiraConnectionService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<ConnectionView> CreateAsync(string baseUrl, string email, string apiToken, CancellationToken ct = default)
    {
        // EndUserId is auto-stamped from the current tenant on SaveChanges.
        var connection = new JiraConnection
        {
            Id = Guid.NewGuid(),
            BaseUrl = baseUrl,
            Email = email,
            ApiTokenEncrypted = apiToken, // plain for now; KMS-encrypted in Phase 4
            CreatedAt = _clock.UtcNow
        };
        _db.JiraConnections.Add(connection);
        await _db.SaveChangesAsync(ct);
        return ToView(connection);
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

    private static ConnectionView ToView(JiraConnection c) => new(c.Id, c.BaseUrl, c.Email, c.CreatedAt);
}
