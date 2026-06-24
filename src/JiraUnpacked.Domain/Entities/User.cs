using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A platform account — either an Admin (operator) or an End User (tenant).
/// REQUIREMENTS §5.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stored normalized (lowercased, trimmed) via <see cref="NormalizeEmail"/> so the
    /// unique index gives case-insensitive uniqueness and login lookups match regardless
    /// of the casing the client typed. Writers must normalize before persisting.
    /// </summary>
    public string Email { get; set; } = null!;

    public string Username { get; set; } = null!;

    /// <summary>Argon2/bcrypt hash only — never the plaintext (FR-A6).</summary>
    public string PasswordHash { get; set; } = null!;

    public UserRole Role { get; set; }

    public UserStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Canonical form of an email for storage and lookup: trimmed and lowercased
    /// (invariant). Centralized so registration and login agree on one normalization.
    /// </summary>
    public static string NormalizeEmail(string email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();
}
