using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class EmailVerificationService : IEmailVerificationService
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _emailSender;
    private readonly IClock _clock;

    public EmailVerificationService(AppDbContext db, IEmailSender emailSender, IClock clock)
    {
        _db = db;
        _emailSender = emailSender;
        _clock = clock;
    }

    public async Task<VerificationResult> VerifyAsync(string email, string code, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null || user.Status != UserStatus.PendingVerification)
        {
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        var verification = await _db.EmailVerifications
            .Where(v => v.UserId == user.Id && v.ConsumedAt == null)
            .OrderByDescending(v => v.ExpiresAt)
            .FirstOrDefaultAsync(ct);

        if (verification is null || verification.ExpiresAt <= now)
        {
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        // Reserve this attempt BEFORE comparing, with a conditional increment the database applies
        // atomically. A read-modify-write (load Attempts, compare, Attempts++, save) let N
        // concurrent guesses all read the same count and all get an answer; this way at most
        // MaxAttempts submissions are ever checked against one code, however they are timed.
        var reserved = await _db.EmailVerifications
            .Where(v => v.Id == verification.Id && v.Attempts < EmailVerification.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Attempts, v => v.Attempts + 1), ct);
        if (reserved == 0)
        {
            return VerificationResult.Fail(VerificationError.TooManyAttempts);
        }

        // Constant-time compare of the SHA-256 hashes (avoid a timing side-channel).
        var submittedHash = Convert.FromBase64String(EmailVerification.HashCode(code));
        var storedHash = Convert.FromBase64String(verification.CodeHash);
        if (!CryptographicOperations.FixedTimeEquals(submittedHash, storedHash))
        {
            return VerificationResult.Fail(VerificationError.Invalid);
        }

        user.Status = UserStatus.Active;
        verification.ConsumedAt = now;
        await _db.SaveChangesAsync(ct);
        return VerificationResult.Ok();
    }

    public async Task ResendAsync(string email, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = User.NormalizeEmail(email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);
        if (user is null || user.Status != UserStatus.PendingVerification)
        {
            return; // silent no-op — don't reveal whether the account exists/is pending
        }

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Lock the account row so concurrent resends for one account serialize: without it two
        // requests could both pass the throttle check below and both mail a code.
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"SELECT 1 FROM ""Users"" WHERE ""Id"" = {user.Id} FOR UPDATE", ct);

        // FR-A1 "resend is throttled": the per-IP rate limiter alone lets anyone who rotates
        // addresses mail an account a code every few seconds. There is no CreatedAt column, so a
        // code's issue time is its expiry minus the shared lifetime. Silent, like every other
        // resend outcome: the 202 must not reveal that this account exists and is pending.
        var newestExpiry = await _db.EmailVerifications
            .Where(v => v.UserId == user.Id)
            .MaxAsync(v => (DateTime?)v.ExpiresAt, ct);
        if (newestExpiry is { } expiry
            && expiry - EmailVerification.CodeLifetime > now - EmailVerification.ResendCooldown)
        {
            return;
        }

        // Invalidate any outstanding codes, then issue a fresh one.
        await _db.EmailVerifications
            .Where(v => v.UserId == user.Id && v.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ConsumedAt, now), ct);

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _db.EmailVerifications.Add(new EmailVerification
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CodeHash = EmailVerification.HashCode(code),
            ExpiresAt = now + EmailVerification.CodeLifetime
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Sent after the commit: the transaction (and the row lock) is not held across SMTP.
        await _emailSender.SendVerificationCodeAsync(normalizedEmail, code, ct);
    }
}
