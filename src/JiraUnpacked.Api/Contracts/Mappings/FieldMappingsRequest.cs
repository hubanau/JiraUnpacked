using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Api.Contracts.Mappings;

public sealed record FieldMappingItem(FieldRole Role, string JiraFieldId);
public sealed record FieldMappingsRequest(IReadOnlyList<FieldMappingItem> Mappings);
