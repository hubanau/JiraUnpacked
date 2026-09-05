using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
namespace JiraUnpacked.Domain.Entities;
public sealed class SyncRun : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid EndUserId { get; set; }
    public Guid JiraProjectId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public SyncRunStatus Status { get; set; }
    public SyncRunKind Kind { get; set; }
    public int IssuesProcessed { get; set; }
    public string? Error { get; set; }
    public DateTime? NextRunAt { get; set; }

    /// <summary>
    /// Which SCOPE this run extracted — a hash of the JQL it actually ran (or of the
    /// whole-project default when the project has no query of its own).
    ///
    /// It exists so an incremental watermark cannot be inherited across a change of scope. A run
    /// only tells you "everything in THIS scope is current as of then"; after the queries change,
    /// the new scope has never been fetched, and reusing the old timestamp asks Jira only for what
    /// changed recently — silently skipping every older issue the new query covers.
    ///
    /// Null on runs made before this column existed, which is deliberate: those include the
    /// silent no-ops that fetched nothing at all, and none of them may seed a watermark.
    /// </summary>
    public string? ScopeFingerprint { get; set; }
}
