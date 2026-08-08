namespace JiraUnpacked.Infrastructure.Sync;

public sealed class SyncSchedulerOptions
{
    public const string SectionName = "Sync";
    public bool SchedulerEnabled { get; set; } = true;

    /// <summary>A Running run older than this is a crashed/stuck sync — the reaper marks it Failed,
    /// unblocking the project (the partial unique index permits one Running run per project).</summary>
    public int StuckRunTimeoutMinutes { get; set; } = 120;
}
