using JiraUnpacked.Domain.Abstractions.Jira;
using JiraUnpacked.Domain.Enums;

namespace JiraUnpacked.Domain.Abstractions;

public sealed record FieldMappingInput(FieldRole Role, string JiraFieldId);
public sealed record StatusSemanticInput(string JiraStatusId, string StatusName, StatusClass Class);

public interface IProjectMappingService
{
    Task<IReadOnlyList<JiraStatus>?> GetStatusesAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> ReplaceFieldMappingsAsync(Guid projectId, IReadOnlyList<FieldMappingInput> items, CancellationToken ct = default);
    Task<IReadOnlyList<FieldMappingInput>?> GetFieldMappingsAsync(Guid projectId, CancellationToken ct = default);
    Task<bool?> ReplaceStatusSemanticsAsync(Guid projectId, IReadOnlyList<StatusSemanticInput> items, CancellationToken ct = default);
    Task<IReadOnlyList<StatusSemanticInput>?> GetStatusSemanticsAsync(Guid projectId, CancellationToken ct = default);
}
