namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// An email-verification challenge for a <see cref="User"/>. The code is stored
/// hashed; it expires and is consumed once (FR-A1). REQUIREMENTS §5.
/// </summary>
public class EmailVerification
{
    /// <summary>
    /// How long a code stays redeemable. Shared by every writer: with no CreatedAt column, a
    /// code's issue time is derived as <see cref="ExpiresAt"/> minus this, so a writer using a
    /// different lifetime would silently skew the resend throttle.
    /// </summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    /// <summary>A resend within this long of the newest code is a silent no-op (FR-A1).</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    /// <summary>Submissions checked against one code before it stops accepting any.</summary>
    public const int MaxAttempts = 5;

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Hash of the verification code — never the plaintext.</summary>
    public string CodeHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    /// <summary>When the code was redeemed; null until consumed.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// Submissions checked against this code, capped at <see cref="MaxAttempts"/> to throttle
    /// guessing. Reserved atomically BEFORE the comparison, so concurrent guesses can never
    /// between them get more than <see cref="MaxAttempts"/> answers.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Canonical hash of a verification code for storage/comparison. Codes are
    /// low-entropy, so online attempts are additionally rate-limited and expire.
    /// </summary>
    public static string HashCode(string code) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
}
