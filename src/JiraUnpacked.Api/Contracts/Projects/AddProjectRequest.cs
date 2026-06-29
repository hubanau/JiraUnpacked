namespace JiraUnpacked.Api.Contracts.Projects;
public sealed record AddProjectRequest(string ProjectKey, int SyncIntervalMinutes);
