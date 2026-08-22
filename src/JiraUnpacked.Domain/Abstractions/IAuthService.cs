namespace JiraUnpacked.Domain.Abstractions;

public sealed record LoginResult(
    bool Succeeded,
    string? AccessToken,
    DateTime? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    /// <summary>When the refresh token itself expires, so the cookie can expire with it.</summary>
    DateTime? RefreshTokenExpiresAtUtc = null);

public interface IAuthService
{
    Task<LoginResult> LoginAsync(string emailOrUsername, string password, CancellationToken ct = default);

    Task<LoginResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    Task LogoutAsync(string refreshToken, CancellationToken ct = default);
}
