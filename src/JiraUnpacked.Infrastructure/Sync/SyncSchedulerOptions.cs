namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncSchedulerOptions
{
    public const string SectionName = "Sync";
    public bool SchedulerEnabled { get; set; } = true;
}
