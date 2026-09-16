using JiraUnpacked.Domain.Abstractions.Jira;
namespace JiraUnpacked.Domain.Abstractions;
public interface IChangelogIngestService
{
    /// <param name="onProgress">
    /// Called after each batch with (done, total) issues. Optional, and awaited: the caller
    /// persists progress, so reporting has to be able to hit the database. This phase re-reads
    /// every stored issue and fetches extra changelog pages for the ones whose history did not
    /// fit inline, which on a large project runs for many minutes with nothing else to show for
    /// itself.
    /// </param>
    Task IngestProjectAsync(
        Guid projectId, JiraCredentials creds,
        Func<int, int, CancellationToken, Task>? onProgress = null,
        CancellationToken ct = default);
}
