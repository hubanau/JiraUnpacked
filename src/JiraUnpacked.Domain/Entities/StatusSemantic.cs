using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;

public sealed class StatusSemantic : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public string JiraStatusId { get; set; } = default!;
    public string StatusName { get; set; } = default!;
    public StatusClass Class { get; set; }
}
