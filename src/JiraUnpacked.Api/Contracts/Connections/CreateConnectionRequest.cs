namespace JiraUnpacked.Api.Contracts.Connections;

public sealed record CreateConnectionRequest(string BaseUrl, string Email, string ApiToken);
