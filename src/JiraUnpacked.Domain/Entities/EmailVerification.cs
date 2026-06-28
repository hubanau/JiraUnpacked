namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// An email-verification challenge for a <see cref="User"/>. The code is stored
/// hashed; it expires and is consumed once (FR-A1). REQUIREMENTS §5.
/// </summary>
public class EmailVerification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Hash of the verification code — never the plaintext.</summary>
    public string CodeHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    /// <summary>When the code was redeemed; null until consumed.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Number of incorrect code submissions; capped to throttle guessing.</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Canonical hash of a verification code for storage/comparison. Codes are
    /// low-entropy, so online attempts are additionally rate-limited and expire.
    /// </summary>
    public static string HashCode(string code) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
}
