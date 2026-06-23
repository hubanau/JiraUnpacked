namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A revocable refresh token for a <see cref="User"/>. Only the SHA-256 hash of
/// the issued token is stored. Rotation links the old token to its replacement.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 hash of the issued token — never the raw token.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Set when the token is revoked (e.g. on rotation or block); null while active.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>On rotation, the hash of the token that superseded this one.</summary>
    public string? ReplacedByTokenHash { get; set; }
}
