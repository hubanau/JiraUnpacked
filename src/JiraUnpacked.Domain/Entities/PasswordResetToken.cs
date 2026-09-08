namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A single-use password-reset challenge for a <see cref="User"/>.
///
/// Unlike <see cref="EmailVerification"/>'s 6-digit code this is high-entropy, because the two
/// flows differ in what a correct guess buys: verification activates an account the holder
/// already proved they own, whereas a reset HANDS OVER an account. A low-entropy code would let
/// an attacker request fresh challenges and grind a 1,000,000 space a few guesses at a time.
///
/// Only the hash is stored, exactly as <see cref="RefreshToken"/> does.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 hash of the issued token — never the raw token.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the token was redeemed or superseded; null while it can still be used.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Canonical hash for storage and lookup.</summary>
    public static string HashToken(string rawToken) =>
        Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawToken)));
}
