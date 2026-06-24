namespace JiraUnpacked.Domain.Abstractions;

public sealed record LoginResult(
    bool Succeeded,
    string? AccessToken,
    DateTime? AccessTokenExpiresAtUtc,
    string? RefreshToken);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default);
}
