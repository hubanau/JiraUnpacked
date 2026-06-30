using System.ComponentModel.DataAnnotations;

namespace JiraUnpacked.Api.Contracts.Projects;

public sealed record AddProjectRequest([Required] string ProjectKey, int SyncIntervalMinutes);
