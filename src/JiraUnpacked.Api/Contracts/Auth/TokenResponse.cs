namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc, string RefreshToken);
