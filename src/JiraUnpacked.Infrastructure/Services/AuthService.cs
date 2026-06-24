using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly ITokenService _tokens;
    private readonly IClock _clock;

    public AuthService(AppDbContext db, IPasswordHasher hasher, ITokenService tokens, IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == emailOrUsername || u.Username == emailOrUsername, ct);

        // Uniform failure for unknown user, non-active status, or wrong password
        // (no enumeration; same response for every reason).
        if (user is null || user.Status != UserStatus.Active || !_hasher.Verify(password, user.PasswordHash))
        {
            return new LoginResult(false, null, null, null);
        }

        var access = _tokens.CreateAccessToken(user);
        var refresh = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refresh.TokenHash,
            CreatedAt = _clock.UtcNow,
            ExpiresAt = refresh.ExpiresAtUtc
        });
        await _db.SaveChangesAsync(ct);

        return new LoginResult(true, access.Token, access.ExpiresAtUtc, refresh.Token);
    }
}
