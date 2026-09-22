using System.ComponentModel.DataAnnotations;

namespace JiraUnpacked.Api.Contracts.Connections;

/// <summary>Rotates a connection's API token. Email is optional; omitted, the stored one is kept.</summary>
public sealed record UpdateConnectionRequest([EmailAddress] string? Email, [Required] string ApiToken);
