namespace JiraUnpacked.Api.Contracts.Auth;

/// <summary>
/// Deliberately carries NO refresh token. The refresh token is delivered only as an httpOnly
/// cookie, so script cannot read it — including at sign-in, which is why it is absent from
/// this body rather than merely also set as a cookie.
/// </summary>
public sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc);
