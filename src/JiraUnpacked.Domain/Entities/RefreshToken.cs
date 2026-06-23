namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A revocable refresh token for a <see cref="User"/>. Only the SHA-256 hash of
/// the issued token is stored. Rotation links the old token to its replacement.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
}
