using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class PasswordResetService : IPasswordResetService
{
    /// <summary>
    /// Short, because the window is the whole exposure: anyone who reaches the mailbox within
    /// it owns the account. Long enough to survive a slow mail relay.
    /// </summary>
    private const int TokenTtlMinutes = 30;

    /// <summary>256 bits. Not guessable at any request volume, which is the point — see the entity.</summary>
    private const int TokenBytes = 32;

    private readonly AppDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly IClock _clock;
    private readonly AppOptions _app;
    private readonly ILogger<PasswordResetService> _logger;

    public PasswordResetService(
        AppDbContext db, IEmailSender emailSender, IPasswordHasher hasher, PasswordPolicy policy,
        IClock clock, IOptions<AppOptions> app, ILogger<PasswordResetService> logger)
    {
        _db = db;
        _emailSender = emailSender;
        _hasher = hasher;
        _policy = policy;
        _clock = clock;
        _app = app.Value;
        _logger = logger;
    }

    public async Task RequestAsync(string email, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

        // Silent no-op for an unknown address, a blocked account, or one that never verified.
        //
        // PendingVerification is excluded deliberately: letting it reset would hand the account
        // to whoever asked, without them ever proving they own the address — a second door
        // around the verification the registration flow insists on. Blocked is excluded because
        // a reset would otherwise undo an administrator's block.
        if (user is null || user.Status != UserStatus.Active) return;

        // One live token per user, so an older email cannot be replayed after a newer request.
        await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, now), ct);

        var rawToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = PasswordResetToken.HashToken(rawToken),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(TokenTtlMinutes),
        });
        await _db.SaveChangesAsync(ct);

        await _emailSender.SendPasswordResetAsync(normalizedEmail, BuildLink(rawToken), ct);
    }

    /// <summary>
    /// Built from configuration, NEVER from the request's Host header. Deriving it from the
    /// request is host-header injection: an attacker submits a reset for someone else's address
    /// with a forged Host, and the victim receives a genuine email carrying a valid token that
    /// points at the attacker's server.
    /// </summary>
    private string BuildLink(string rawToken) =>
        $"{_app.BaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(rawToken)}";

    public async Task<PasswordResetResult> ResetAsync(
        string token, string newPassword, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        // Looked up BY HASH: the raw token never goes near the database, and an equality match
        // on the hash is what makes the comparison constant-time in effect.
        var hash = PasswordResetToken.HashToken(token ?? string.Empty);
        var reset = await _db.PasswordResetTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Unknown, already used, and expired collapse into one answer on purpose — each of the
        // three is a fact about somebody else's account.
        if (reset is null || reset.ConsumedAt is not null || reset.ExpiresAt <= now)
            return PasswordResetResult.InvalidToken();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == reset.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return PasswordResetResult.InvalidToken();

        // Policy is checked BEFORE the token is consumed, so a user who fumbles the new password
        // can try again with the same link instead of having to request another one.
        var policy = _policy.Validate(newPassword);
        if (!policy.IsValid) return PasswordResetResult.Weak(policy.Errors);

        user.PasswordHash = _hasher.Hash(newPassword);
        reset.ConsumedAt = now;

        // The step that is easy to omit. A reset is often prompted by someone else having got
        // in; leaving their session refreshable defeats the entire exercise.
        await _db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        await _db.SaveChangesAsync(ct);

        // The user id only — never the token, never the address.
        _logger.LogInformation("Password reset completed for user {UserId}.", user.Id);
        return PasswordResetResult.Ok();
    }

    /// <summary>URL-safe, so the token survives a query string without escaping surprises.</summary>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
