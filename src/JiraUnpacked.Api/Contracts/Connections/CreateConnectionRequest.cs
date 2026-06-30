using System.ComponentModel.DataAnnotations;

namespace JiraUnpacked.Api.Contracts.Connections;

public sealed record CreateConnectionRequest([Required, Url] string BaseUrl, string Email, string ApiToken);
