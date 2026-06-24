namespace JiraUnpacked.Domain.Entities;

/// <summary>
/// A single-use, expiring invite code created by an Admin. End Users may only
/// register with a valid code (FR-A2, FR-A4). REQUIREMENTS §5.
/// </summary>
public class InviteCode
{
    public Guid Id { get; set; }

    public string Code { get; set; } = null!;

    /// <summary>The Admin (<see cref="User"/>) who created this code.</summary>
    public Guid CreatedByAdminId { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>The End User who redeemed it; null until used.</summary>
    public Guid? UsedByUserId { get; set; }

    public DateTime? UsedAt { get; set; }
}
