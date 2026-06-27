namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record VerifyRequest(string Email, string Code);
