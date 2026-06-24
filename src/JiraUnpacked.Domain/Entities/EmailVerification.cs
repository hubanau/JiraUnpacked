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
}
