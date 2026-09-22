using System.Security.Cryptography;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Domain.Security;
using JiraUnpacked.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Infrastructure.Services;

public sealed class RegistrationService : IRegistrationService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly PasswordPolicy _policy;
    private readonly IEmailSender _emailSender;
    private readonly IClock _clock;

    public RegistrationService(
        AppDbContext db, IPasswordHasher hasher, PasswordPolicy policy, IEmailSender emailSender, IClock clock)
    {
        _db = db;
        _hasher = hasher;
        _policy = policy;
        _emailSender = emailSender;
        _clock = clock;
    }

    public async Task<RegistrationResult> RegisterAsync(
        string inviteCode, string email, string username, string password, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;

        // Shape checks first: no database work, and nothing — least of all the invite — is
        // touched for input that could never become an account. The username rule is also what
        // keeps login unambiguous (a username can never contain '@').
        if (!AccountInputRules.IsValidEmail(email))
        {
            return RegistrationResult.Fail(RegistrationError.InvalidEmail);
        }
        if (!AccountInputRules.IsValidUsername(username))
        {
            return RegistrationResult.Fail(RegistrationError.InvalidUsername);
        }

        var normalizedEmail = User.NormalizeEmail(email);

        // Reject an invalid/expired/used invite before creating anything (FR-A2).
        var inviteOk = await _db.InviteCodes.AnyAsync(
            c => c.Code == inviteCode && c.UsedByUserId == null && c.ExpiresAt > now, ct);
        if (!inviteOk)
        {
            return RegistrationResult.Fail(RegistrationError.InvalidInviteCode);
        }

        var policy = _policy.Validate(password);
        if (!policy.IsValid)
        {
            return RegistrationResult.Weak(policy.Errors);
        }

        var taken = await _db.Users.AnyAsync(
            u => u.Email == normalizedEmail || u.Username == username, ct);
        if (taken)
        {
            return RegistrationResult.Fail(RegistrationError.EmailOrUsernameTaken);
        }

        var userId = Guid.NewGuid();
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Insert the user first so it exists as the FK target for the invite's UsedByUserId.
        _db.Users.Add(new User
        {
            Id = userId,
            Email = normalizedEmail,
            Username = username,
            PasswordHash = _hasher.Hash(password),
            Role = UserRole.EndUser,
            Status = UserStatus.PendingVerification,
            CreatedAt = now
        });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A concurrent registration claimed the same email/username between the
            // pre-check and this insert; the unique index is the real guard, so map
            // the race to the same clean result as the pre-check.
            await tx.RollbackAsync(ct);
            return RegistrationResult.Fail(RegistrationError.EmailOrUsernameTaken);
        }

        // Atomically consume the invite; if a concurrent registration already claimed
        // it, zero rows update and we roll back — a code can never be used twice.
        var claimed = await _db.InviteCodes
            .Where(c => c.Code == inviteCode && c.UsedByUserId == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.UsedByUserId, userId)
                .SetProperty(c => c.UsedAt, now), ct);
        if (claimed == 0)
        {
            await tx.RollbackAsync(ct);
            return RegistrationResult.Fail(RegistrationError.InvalidInviteCode);
        }

        _db.EmailVerifications.Add(new EmailVerification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CodeHash = EmailVerification.HashCode(code),
            ExpiresAt = now + EmailVerification.CodeLifetime
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Send the code after the account is committed (don't hold the transaction on I/O).
        await _emailSender.SendVerificationCodeAsync(normalizedEmail, code, ct);

        return RegistrationResult.Ok();
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };
}
