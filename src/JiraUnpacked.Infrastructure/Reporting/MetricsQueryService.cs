// src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using System.Linq.Expressions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// FR-E2 curated metrics, computed on the fly from stored data — no Jira calls. Every read goes
/// through the injected tenant-scoped AppDbContext (EF query filter + Postgres RLS).
/// </summary>
public sealed class MetricsQueryService : IMetricsQueryService
{
    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly IIssueFilterCompiler _filter;
    private readonly IReportCache _cache;

    public MetricsQueryService(
        AppDbContext db, IClock clock, IIssueFilterCompiler filter, IReportCache cache)
    {
        _db = db;
        _clock = clock;
        _filter = filter;
        _cache = cache;
    }

    /// <summary>
    /// The latest completed sync or rederive. Both go through SyncOrchestrator and stamp
    /// FinishedAt, so this moves whenever the modeled data does — which is what lets the
    /// timeline fold be cached without ever serving a stale number.
    /// </summary>
    private async Task<DateTime?> SyncWatermarkAsync(Guid projectId, CancellationToken ct) =>
        await _db.SyncRuns
            .Where(r => r.JiraProjectId == projectId && r.Status == SyncRunStatus.Succeeded)
            .MaxAsync(r => (DateTime?)r.FinishedAt, ct);

    /// <summary>
    /// Everything a timeline answer depends on, in one string. The semantics map is in here
    /// because it decides which status counts as Active or Done: edit it and every duration
    /// changes, with no sync involved. Ordered so the same map always produces the same text.
    /// </summary>
    private static string Fingerprint(
        DateTime? watermark, Dictionary<string, StatusClass> semantics, ReportFilter? filter)
    {
        var classes = string.Join(
            "|", semantics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        var conditions = filter is null
            ? string.Empty
            : System.Text.Json.JsonSerializer.Serialize(filter, SavedFilterService.DefinitionJson);

        return $"{watermark:O}#{classes}#{conditions}";
    }

    /// <summary>
    /// Resolves a request into either a predicate over the population or a failure. The project
    /// check runs FIRST so a bad filter aimed at another tenant's project still answers 404
    /// rather than leaking the difference between "no such project" and "bad filter".
    /// </summary>
    private async Task<(Expression<Func<Issue, bool>>? Population, ReportResult<T>? Failure)>
        ResolvePopulationAsync<T>(Guid projectId, ReportFilter? filter, CancellationToken ct) where T : class
    {
        if (!await ProjectExistsAsync(projectId, ct)) return (null, ReportResult<T>.NotFound());

        var compiled = await _filter.CompileAsync(filter, ct);
        return compiled.Error is not null
            ? (null, ReportResult<T>.Invalid(compiled.Error))
            : (compiled.Predicate, null);
    }

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    /// <summary>The project's status-name -> class map. Case-insensitive, per the spec.</summary>
    private async Task<Dictionary<string, StatusClass>> LoadSemanticsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await _db.StatusSemantics
            .Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.StatusName, s.Class })
            .ToListAsync(ct);

        var map = new Dictionary<string, StatusClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.StatusName] = r.Class;
        return map;
    }

    /// <summary>Projected issue row — no navigation properties, Spec 1's correlated-subquery style.</summary>
    private sealed record IssueRow(
        string JiraIssueId, string JiraKey, string? Summary, string? CurrentStatus,
        decimal? StoryPoints, DateTime JiraCreatedAt);

    /// <summary>
    /// One project's folded timelines. Shared between requests by the cache, so every member is
    /// read-only by type: a caller that mutated it would corrupt the next request's answer.
    /// </summary>
    private sealed record TimelineSnapshot(
        IReadOnlyList<IssueRow> Issues, IReadOnlyDictionary<string, IssueTimeline> Timelines);

    public async Task<ReportResult<StoryCountsReport>> GetStoryCountsAsync(
        Guid projectId, ReportFilter? filter = null, CancellationToken ct = default)
    {
        var (population, failure) = await ResolvePopulationAsync<StoryCountsReport>(projectId, filter, ct);
        if (failure is not null) return failure;

        var semantics = await LoadSemanticsAsync(projectId, ct);

        // Current status comes straight off Issue.StatusId — this metric never loads change history.
        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Where(population!)
            .Select(i => new
            {
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints
            })
            .ToListAsync(ct);

        var byStatus = rows
            .GroupBy(r => r.Status)
            .Select(g => new StatusCount(
                g.Key,
                StatusTimelineEngine.Classify(g.Key, semantics).ToString(),
                g.Count(),
                g.Sum(x => x.StoryPoints ?? 0m)))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Status)
            .ToList();

        // Totals are the FILTERED totals — a reader who sees a filter applied expects the
        // headline numbers to describe the same population as the bars.
        return ReportResult<StoryCountsReport>.Ok(
            new StoryCountsReport(byStatus, rows.Count, rows.Sum(r => r.StoryPoints ?? 0m)));
    }

    /// <summary>
    /// Loads everything the timeline metrics need, project-scoped: the semantics map, the
    /// projected issues, and their status transitions — then folds each issue through the engine.
    /// Story counts deliberately does NOT call this: it needs no change history.
    /// </summary>
    /// <summary>
    /// The cached entry point. Two cheap queries (semantics + watermark) decide whether the two
    /// expensive ones are needed at all — which is what collapses the metrics page's three
    /// separate requests for the same fold into one load.
    /// </summary>
    private async Task<TimelineSnapshot> LoadTimelinesAsync(
        Guid projectId, Expression<Func<Issue, bool>> population, ReportFilter? filter, CancellationToken ct)
    {
        var semantics = await LoadSemanticsAsync(projectId, ct);
        var watermark = await SyncWatermarkAsync(projectId, ct);

        return await _cache.GetOrCreateAsync(
            "timelines", projectId, Fingerprint(watermark, semantics, filter),
            token => BuildTimelinesAsync(projectId, population, semantics, token), ct);
    }

    private async Task<TimelineSnapshot>
        BuildTimelinesAsync(
            Guid projectId, Expression<Func<Issue, bool>> population,
            Dictionary<string, StatusClass> semantics, CancellationToken ct)
    {
        var issues = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Where(population)
            .Select(i => new IssueRow(
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints,
                i.JiraCreatedAt))
            .ToListAsync(ct);

        // Change history stays scoped to the PROJECT, not to the filtered issue ids: the fold
        // below is per-issue, so a wider load cannot change a number, whereas an IN list of
        // issue ids would grow to the size of the project.
        //
        // "status" is the field name Jira's changelog uses, and what the derivation stores.
        // ThenBy(JiraHistoryId) is a stable tiebreaker: Jira bulk edits can produce two
        // transitions on the same issue with an identical ChangedAt, and without a deterministic
        // secondary sort the DB could return them in either order, flipping which one the engine
        // treats as "current" between two identical requests.
        var history = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId && h.Field == "status")
            .OrderBy(h => h.ChangedAt).ThenBy(h => h.JiraHistoryId)
            .Select(h => new { h.JiraIssueId, h.ChangedAt, h.FromValue, h.ToValue })
            .ToListAsync(ct);

        var byIssue = history
            .GroupBy(h => h.JiraIssueId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<StatusTransition>)g
                    .Select(h => new StatusTransition(h.ChangedAt, h.FromValue, h.ToValue))
                    .ToList());

        var timelines = new Dictionary<string, IssueTimeline>();
        foreach (var i in issues)
        {
            var transitions = byIssue.TryGetValue(i.JiraIssueId, out var t)
                ? t
                : (IReadOnlyList<StatusTransition>)Array.Empty<StatusTransition>();
            timelines[i.JiraIssueId] =
                StatusTimelineEngine.Build(i.JiraCreatedAt, i.CurrentStatus, transitions, semantics);
        }

        return new TimelineSnapshot(issues, timelines);
    }

    public async Task<ReportResult<TimeInProgressReport>> GetTimeInProgressAsync(
        Guid projectId, ReportFilter? filter = null, CancellationToken ct = default)
    {
        var (population, failure) = await ResolvePopulationAsync<TimeInProgressReport>(projectId, filter, ct);
        if (failure is not null) return failure;

        var snapshot = await LoadTimelinesAsync(projectId, population!, filter, ct);

        var durations = new List<double>();
        var excluded = 0;
        foreach (var t in snapshot.Timelines.Values)
        {
            // Cycle time needs BOTH ends, and Done must come after Active.
            if (t.FirstActiveAt is DateTime a && t.FirstDoneAt is DateTime d && d > a)
                durations.Add((d - a).TotalDays);
            else
                excluded++;
        }

        // AverageDays stays null (never 0) when nothing qualifies — including when it is the
        // FILTER that excluded every completed issue. "Not enough data" and "instant" are
        // different answers and a filtered view must not convert one into the other.
        return ReportResult<TimeInProgressReport>.Ok(new TimeInProgressReport(
            durations.Count == 0 ? null : durations.Average(),
            durations.Count,
            excluded));
    }

    public async Task<ReportResult<ThroughputReport>> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to,
        ReportFilter? filter = null, CancellationToken ct = default)
    {
        var (population, failure) = await ResolvePopulationAsync<ThroughputReport>(projectId, filter, ct);
        if (failure is not null) return failure;

        var snapshot = await LoadTimelinesAsync(projectId, population!, filter, ct);

        // Resolve the window: both ends snap OUTWARD to bucket boundaries; the default is the
        // last 12 buckets ending with the bucket containing "now".
        var toStart = FloorTo(ToUtc(to) ?? _clock.UtcNow, bucket);
        var fromStart = from is DateTime f ? FloorTo(ToUtc(f)!.Value, bucket) : StepBack(toStart, bucket, 11);

        var counts = new Dictionary<DateTime, int>();
        foreach (var t in snapshot.Timelines.Values)
        {
            if (t.FirstDoneAt is not DateTime done) continue;
            var b = FloorTo(done, bucket);
            if (b < fromStart || b > toStart) continue;
            counts[b] = counts.TryGetValue(b, out var c) ? c + 1 : 1;
        }

        // Emit every bucket in range, including empty ones, so the series is dense/chartable.
        var buckets = new List<ThroughputBucket>();
        for (var cursor = fromStart; cursor <= toStart; cursor = NextBucket(cursor, bucket))
            buckets.Add(new ThroughputBucket(cursor, counts.TryGetValue(cursor, out var c) ? c : 0));

        return ReportResult<ThroughputReport>.Ok(new ThroughputReport(
            bucket.ToString().ToLowerInvariant(), fromStart, toStart, buckets));
    }

    private static DateTime? ToUtc(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static DateTime FloorTo(DateTime utc, ThroughputBucketSize bucket) => bucket switch
    {
        ThroughputBucketSize.Day => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
        ThroughputBucketSize.Week => StartOfIsoWeek(utc),
        ThroughputBucketSize.Month => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };

    /// <summary>ISO week starts Monday.</summary>
    private static DateTime StartOfIsoWeek(DateTime utc)
    {
        var day = new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc);
        var delta = (7 + (int)day.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return day.AddDays(-delta);
    }

    private static DateTime NextBucket(DateTime start, ThroughputBucketSize bucket) => bucket switch
    {
        ThroughputBucketSize.Day => start.AddDays(1),
        ThroughputBucketSize.Week => start.AddDays(7),
        ThroughputBucketSize.Month => start.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };

    private static DateTime StepBack(DateTime start, ThroughputBucketSize bucket, int steps)
    {
        var cursor = start;
        for (var i = 0; i < steps; i++)
            cursor = bucket switch
            {
                ThroughputBucketSize.Day => cursor.AddDays(-1),
                ThroughputBucketSize.Week => cursor.AddDays(-7),
                ThroughputBucketSize.Month => cursor.AddMonths(-1),
                _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
            };
        return cursor;
    }

    public async Task<ReportResult<StatusAgingReport>> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize,
        ReportFilter? filter = null, CancellationToken ct = default)
    {
        var (population, failure) = await ResolvePopulationAsync<StatusAgingReport>(projectId, filter, ct);
        if (failure is not null) return failure;

        var snapshot = await LoadTimelinesAsync(projectId, population!, filter, ct);
        var now = _clock.UtcNow;

        // Population: everything not currently Done.
        var open = new List<AgingItem>();
        foreach (var i in snapshot.Issues)
        {
            var t = snapshot.Timelines[i.JiraIssueId];
            if (t.CurrentClass == StatusClass.Done) continue;

            open.Add(new AgingItem(
                i.JiraIssueId, i.JiraKey, i.Summary,
                t.CurrentStatus, t.CurrentClass.ToString(),
                (now - t.CurrentEnteredAt).TotalDays, t.CurrentEnteredAt));
        }

        // Histogram spans the WHOLE population, deliberately computed before paging.
        var histogram = BuildHistogram(open);

        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var ordered = open
            .OrderByDescending(a => a.AgeDays)
            .ThenBy(a => a.JiraKey)
            .ToList();

        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return ReportResult<StatusAgingReport>.Ok(new StatusAgingReport(
            new PagedResult<AgingItem>(items, page, pageSize, ordered.Count), histogram));
    }

    /// <summary>Half-open day bands, so every age falls in exactly one. All five are always emitted.</summary>
    private static IReadOnlyList<AgingBand> BuildHistogram(IReadOnlyList<AgingItem> items)
    {
        var bands = new (string Name, double Lo, double Hi)[]
        {
            // Lower bound is -infinity, not 0: a future-dated CurrentEnteredAt (clock skew, bad
            // Jira timestamp) yields a negative AgeDays, which must still land in exactly one band.
            ("<=1d", double.NegativeInfinity, 1),
            ("1-3d", 1, 3),
            ("3-7d", 3, 7),
            ("1-2w", 7, 14),
            (">2w", 14, double.PositiveInfinity),
        };

        return bands
            .Select(b => new AgingBand(b.Name, items.Count(i => i.AgeDays >= b.Lo && i.AgeDays < b.Hi)))
            .ToList();
    }
}
