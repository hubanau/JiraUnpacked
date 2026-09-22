namespace JiraUnpacked.Domain.Abstractions;

public interface IDerivationService
{
    /// <summary>
    /// Jira-free: rebuilds modeled tables for a project from raw + field mappings. Caller sets the
    /// ambient tenant.
    /// </summary>
    /// <param name="resumeStage">
    /// Which internal stage to re-enter — one of ResumeStage.DeriveIssues / DeriveHistory /
    /// DeriveHierarchy. Null, or any earlier stage, runs all three from the start.
    /// </param>
    /// <param name="resumeCursor">
    /// Position within <paramref name="resumeStage"/>: the last row id a previous attempt
    /// committed. Ignored for any other stage.
    /// </param>
    /// <param name="alreadyDone">Rows a previous attempt finished, so the bar continues.</param>
    /// <param name="batchSize">Rows per commit. Bounds the memory this phase holds.</param>
    /// <param name="onBatch">
    /// Called after each batch with (stage, cursor, done, total). Optional, and awaited: the caller
    /// commits, and it commits the batch's rows and that cursor together, so a cursor can never
    /// outlive the rows it points past. When null the service saves for itself.
    ///
    /// done/total count RAW ROWS rebuilt. Both stages that dominate this phase do one row's work
    /// per row, so counting rows across them keeps a single bar moving through the whole phase
    /// instead of stalling at the seam between them.
    /// </param>
    /// <param name="incrementalSince">
    /// Null rebuilds everything — a rederive, which exists to apply a changed mapping to every
    /// issue. Otherwise only issues whose snapshot is newer than their modelled row, and changelogs
    /// ingested at or after this instant, are rebuilt: a sync that fetched 700 issues must not
    /// re-parse 92,000.
    /// </param>
    Task DeriveProjectAsync(
        Guid projectId,
        string? resumeStage = null,
        string? resumeCursor = null,
        int alreadyDone = 0,
        int batchSize = 10_000,
        Func<string, string, int, int, CancellationToken, Task>? onBatch = null,
        DateTime? incrementalSince = null,
        CancellationToken ct = default);
}
