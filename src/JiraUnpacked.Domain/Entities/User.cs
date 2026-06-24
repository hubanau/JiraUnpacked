using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A platform account — either an Admin (operator) or an End User (tenant).
/// REQUIREMENTS §5.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    public string Email { get; set; } = null!;

    public string Username { get; set; } = null!;

    /// <summary>Argon2/bcrypt hash only — never the plaintext (FR-A6).</summary>
    public string PasswordHash { get; set; } = null!;

    public UserRole Role { get; set; }

    public UserStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
