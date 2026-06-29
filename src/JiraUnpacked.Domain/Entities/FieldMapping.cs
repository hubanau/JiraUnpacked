using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;

public sealed class FieldMapping : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public FieldRole Role { get; set; }
    public string JiraFieldId { get; set; } = default!;
}
