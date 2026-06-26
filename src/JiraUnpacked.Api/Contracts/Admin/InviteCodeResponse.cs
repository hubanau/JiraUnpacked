namespace JiraUnpacked.Api.Contracts.Admin;

public sealed record InviteCodeResponse(string Code, DateTime ExpiresAtUtc);
