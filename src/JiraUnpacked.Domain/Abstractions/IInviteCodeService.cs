namespace JiraUnpacked.Domain.Abstractions;

public sealed record InviteCodeResult(string Code, DateTime ExpiresAtUtc);

public interface IInviteCodeService
{
    Task<InviteCodeResult> CreateAsync(Guid createdByAdminId, CancellationToken ct = default);
}
