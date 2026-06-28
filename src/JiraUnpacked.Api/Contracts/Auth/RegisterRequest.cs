namespace JiraUnpacked.Api.Contracts.Auth;

public sealed record RegisterRequest(string InviteCode, string Email, string Username, string Password);
