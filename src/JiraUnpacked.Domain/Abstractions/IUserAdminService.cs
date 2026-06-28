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

public interface IUserAdminService
{
    Task<BlockResult> BlockAsync(Guid userId, CancellationToken ct = default);
    Task<BlockResult> UnblockAsync(Guid userId, CancellationToken ct = default);
}
