namespace JiraUnpacked.Domain.Enums;

/// <summary>
/// Account lifecycle. Registration creates a <see cref="PendingVerification"/>
/// account that becomes <see cref="Active"/> once the email code is confirmed
/// (FR-A1). Admins can <see cref="Blocked"/> an End User reversibly (FR-A5).
/// </summary>
public enum UserStatus
{
    PendingVerification,
    Active,
    Blocked
}
