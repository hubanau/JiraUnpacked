namespace JiraUnpacked.Domain.Abstractions;

public enum BlockError
{
    None,
    NotFound,
    NotAnEndUser
}

public sealed record BlockResult(bool Succeeded, BlockError Error)
{
    public static BlockResult Ok() => new(true, BlockError.None);
    public static BlockResult Fail(BlockError error) => new(false, error);
}

/// <summary>Never carries PasswordHash — the entity has one and must not be projected.</summary>
public sealed record AdminUserListItem(
    Guid Id, string Email, string Username, string Role, string Status, DateTime CreatedAt);

public interface IUserAdminService
{
    Task<BlockResult> BlockAsync(Guid userId, CancellationToken ct = default);
    Task<BlockResult> UnblockAsync(Guid userId, CancellationToken ct = default);
    Task<PagedResult<AdminUserListItem>> ListAsync(int page, int pageSize, CancellationToken ct = default);
}
