using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class EmailVerificationService : IEmailVerificationService
{
    private const int MaxAttempts = 5;
    private const int CodeTtlMinutes = 15;

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

        if (verification.Attempts >= MaxAttempts)
        {
            return VerificationResult.Fail(VerificationError.TooManyAttempts);
        }

        // Constant-time compare of the SHA-256 hashes (avoid a timing side-channel).
        var submittedHash = Convert.FromBase64String(EmailVerification.HashCode(code));
        var storedHash = Convert.FromBase64String(verification.CodeHash);
        if (!CryptographicOperations.FixedTimeEquals(submittedHash, storedHash))
        {
            verification.Attempts++;
            await _db.SaveChangesAsync(ct);
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
            ExpiresAt = now.AddMinutes(CodeTtlMinutes)
        });
        await _db.SaveChangesAsync(ct);

        await _emailSender.SendVerificationCodeAsync(normalizedEmail, code, ct);
    }
}
