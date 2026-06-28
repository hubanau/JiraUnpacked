namespace JiraUnpacked.Api.Contracts.Connections;

public sealed record ConnectionResponse(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
