using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Identity;

/// <summary>
/// Seeds configured Admin accounts at startup (FR-A3). Idempotent: an admin whose
/// email or username already exists is skipped. There is no admin self-registration.
/// </summary>
public sealed class AdminSeeder
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IClock _clock;
    private readonly SeedOptions _options;

    public AdminSeeder(AppDbContext db, IPasswordHasher hasher, IClock clock, IOptions<SeedOptions> options)
    {
        _db = db;
        _hasher = hasher;
        _clock = clock;
        _options = options.Value;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var admin in _options.Admins)
        {
            if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Username)
                || string.IsNullOrWhiteSpace(admin.Password))
            {
                continue; // skip incomplete entries
            }

            var exists = await _db.Users.AnyAsync(
                u => u.Email == admin.Email || u.Username == admin.Username, ct);
            if (exists)
            {
                continue; // idempotent: already seeded
            }

            _db.Users.Add(new User
            {
                Id = Guid.NewGuid(),
                Email = admin.Email,
                Username = admin.Username,
                PasswordHash = _hasher.Hash(admin.Password),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = _clock.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
    }
}
