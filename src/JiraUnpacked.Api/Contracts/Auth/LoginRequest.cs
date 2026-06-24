namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record LoginRequest(string EmailOrUsername, string Password);
