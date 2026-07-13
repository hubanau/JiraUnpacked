namespace JiraUnpacked.Domain.Abstractions;

public interface IDerivationService
{
    /// <summary>Jira-free: rebuilds modeled tables for a project from raw + field mappings. Caller sets the ambient tenant.</summary>
    Task DeriveProjectAsync(Guid projectId, CancellationToken ct = default);
}
