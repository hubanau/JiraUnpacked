using System.ComponentModel.DataAnnotations;

namespace JiraUnpacked.Api.Contracts.Queries;

public sealed record AddQueryRequest([Required] string Jql);
