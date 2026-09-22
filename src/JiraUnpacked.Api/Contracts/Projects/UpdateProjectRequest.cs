namespace JiraUnpacked.Api.Contracts.Projects;

/// <summary>Either or both may be supplied; a request with neither is rejected.</summary>
public sealed record UpdateProjectRequest(int? SyncIntervalMinutes, bool? SyncPaused);
