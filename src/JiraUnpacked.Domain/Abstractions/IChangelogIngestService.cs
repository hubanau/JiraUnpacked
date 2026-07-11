using JiraUnpacked.Domain.Abstractions.Jira;
namespace JiraUnpacked.Domain.Abstractions;
public interface IChangelogIngestService
{
    Task IngestProjectAsync(Guid projectId, JiraCredentials creds, CancellationToken ct = default);
}
