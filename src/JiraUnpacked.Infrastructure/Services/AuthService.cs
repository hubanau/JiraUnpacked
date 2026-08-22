using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class AuthService : IAuthService
{
    // A valid bcrypt hash (same work factor) verified on the no-such-user path so login
    // timing doesn't reveal whether an account exists. Computed once; not a secret.
    private static readonly string DummyPasswordHash =
        new BcryptPasswordHasher().Hash("login-timing-equalizer");

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
        // Email is stored normalized, so match the normalized form for the email branch
        // (case-insensitive); username stays an exact match.
        var normalizedEmail = User.NormalizeEmail(emailOrUsername);
        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.Email == normalizedEmail || u.Username == emailOrUsername, ct);

        // Always perform exactly one bcrypt verification (against a dummy hash when the
        // user is absent) so response timing is uniform and login can't enumerate accounts.
        var passwordValid = _hasher.Verify(password, user?.PasswordHash ?? DummyPasswordHash);

        // Uniform failure for unknown user, non-active status, or wrong password.
        if (user is null || user.Status != UserStatus.Active || !passwordValid)
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

        return new LoginResult(true, access.Token, access.ExpiresAtUtc, refresh.Token, refresh.ExpiresAtUtc);
    }

    public async Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        var now = _clock.UtcNow;

        // Reject unknown, already-revoked (incl. rotated), or expired tokens.
        if (existing is null || existing.RevokedAt is not null || existing.ExpiresAt <= now)
        {
            return new LoginResult(false, null, null, null);
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == existing.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
        {
            return new LoginResult(false, null, null, null);
        }

        var access = _tokens.CreateAccessToken(user);
        var newRefresh = _tokens.CreateRefreshToken();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Rotate atomically: revoke the presented token only if it's still active. Two
        // concurrent refreshes with the same token serialize here — the loser updates
        // zero rows and is rejected, so a single token can never mint two replacements.
        var revoked = await _db.RefreshTokens
            .Where(t => t.Id == existing.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenHash, newRefresh.TokenHash), ct);

        if (revoked == 0)
        {
            return new LoginResult(false, null, null, null);
        }

        _db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = newRefresh.TokenHash,
            CreatedAt = now,
            ExpiresAt = newRefresh.ExpiresAtUtc
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new LoginResult(true, access.Token, access.ExpiresAtUtc, newRefresh.Token, newRefresh.ExpiresAtUtc);
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        var hash = _tokens.HashRefreshToken(refreshToken);
        var existing = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, ct);

        if (existing is not null)
        {
            existing.RevokedAt = _clock.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        // Idempotent: unknown or already-revoked tokens are a no-op (no error, no info leak).
    }
}
