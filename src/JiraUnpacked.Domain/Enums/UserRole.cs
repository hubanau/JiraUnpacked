namespace JiraUnpacked.Domain.Enums;

/// <summary>
/// Platform role. Admins are platform operators (seeded, not self-registered);
/// the tenant boundary is the End User. See REQUIREMENTS §2.
/// </summary>
public enum UserRole
{
    Admin,
    EndUser
}
