namespace JiraUnpacked.Domain.Abstractions;

public interface IDerivationService
{
    /// <summary>Jira-free: rebuilds modeled tables for a project from raw + field mappings. Caller sets the ambient tenant.</summary>
    /// <param name="onProgress">
    /// Called with (done, total) RAW ROWS rebuilt. Both stages that dominate this phase — modelling
    /// issues, then rebuilding change history — do one row's work per row, so counting rows across
    /// the two keeps a single bar moving through the whole phase instead of stalling at the seam
    /// between them. Optional, and awaited: the caller persists what it reports.
    /// </param>
    Task DeriveProjectAsync(
        Guid projectId,
        Func<int, int, CancellationToken, Task>? onProgress = null,
        CancellationToken ct = default);
}
