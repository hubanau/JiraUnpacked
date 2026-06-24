using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class InviteCodeService : IInviteCodeService
{
    // Codes expire after this many days. Configurable later if needed (YAGNI for now).
    private const int ExpiryDays = 14;

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public InviteCodeService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<InviteCodeResult> CreateAsync(Guid createdByAdminId, CancellationToken ct = default)
    {
        // 24 random bytes -> 32-char base64url string (fits the Code varchar(64) column).
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var expiresAt = _clock.UtcNow.AddDays(ExpiryDays);

        _db.InviteCodes.Add(new InviteCode
        {
            Id = Guid.NewGuid(),
            Code = code,
            CreatedByAdminId = createdByAdminId,
            ExpiresAt = expiresAt
        });
        await _db.SaveChangesAsync(ct);

        return new InviteCodeResult(code, expiresAt);
    }
}
