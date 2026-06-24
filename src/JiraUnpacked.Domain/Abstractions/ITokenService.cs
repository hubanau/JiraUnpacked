using JiraUnpacked.Domain.Entities;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

/// <summary>The raw token is returned to the client; only <see cref="TokenHash"/> is persisted.</summary>
public sealed record RefreshTokenValue(string Token, string TokenHash, DateTime ExpiresAtUtc);

public interface ITokenService
{
    AccessToken CreateAccessToken(User user);
    RefreshTokenValue CreateRefreshToken();
    string HashRefreshToken(string rawToken);
}
