// src/JiraUnpacked.Infrastructure/Reporting/MetricsQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
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

    public MetricsQueryService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
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

    public async Task<StoryCountsReport?> GetStoryCountsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var semantics = await LoadSemanticsAsync(projectId, ct);

        // Current status comes straight off Issue.StatusId — this metric never loads change history.
        var rows = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
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

        return new StoryCountsReport(byStatus, rows.Count, rows.Sum(r => r.StoryPoints ?? 0m));
    }

    /// <summary>
    /// Loads everything the timeline metrics need, project-scoped: the semantics map, the
    /// projected issues, and their status transitions — then folds each issue through the engine.
    /// Story counts deliberately does NOT call this: it needs no change history.
    /// </summary>
    private async Task<(List<IssueRow> Issues, Dictionary<string, IssueTimeline> Timelines)>
        LoadTimelinesAsync(Guid projectId, CancellationToken ct)
    {
        var semantics = await LoadSemanticsAsync(projectId, ct);

        var issues = await _db.Issues
            .Where(i => i.JiraProjectId == projectId)
            .Select(i => new IssueRow(
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
                i.StoryPoints,
                i.JiraCreatedAt))
            .ToListAsync(ct);

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

        return (issues, timelines);
    }

    public async Task<TimeInProgressReport?> GetTimeInProgressAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (_, timelines) = await LoadTimelinesAsync(projectId, ct);

        var durations = new List<double>();
        var excluded = 0;
        foreach (var t in timelines.Values)
        {
            // Cycle time needs BOTH ends, and Done must come after Active.
            if (t.FirstActiveAt is DateTime a && t.FirstDoneAt is DateTime d && d > a)
                durations.Add((d - a).TotalDays);
            else
                excluded++;
        }

        return new TimeInProgressReport(
            durations.Count == 0 ? null : durations.Average(),
            durations.Count,
            excluded);
    }

    public async Task<ThroughputReport?> GetThroughputAsync(
        Guid projectId, ThroughputBucketSize bucket, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (_, timelines) = await LoadTimelinesAsync(projectId, ct);

        // Resolve the window: both ends snap OUTWARD to bucket boundaries; the default is the
        // last 12 buckets ending with the bucket containing "now".
        var toStart = FloorTo(ToUtc(to) ?? _clock.UtcNow, bucket);
        var fromStart = from is DateTime f ? FloorTo(ToUtc(f)!.Value, bucket) : StepBack(toStart, bucket, 11);

        var counts = new Dictionary<DateTime, int>();
        foreach (var t in timelines.Values)
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

        return new ThroughputReport(
            bucket.ToString().ToLowerInvariant(), fromStart, toStart, buckets);
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

    public async Task<StatusAgingReport?> GetStatusAgingAsync(
        Guid projectId, int page, int pageSize, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        var (issues, timelines) = await LoadTimelinesAsync(projectId, ct);
        var now = _clock.UtcNow;

        // Population: everything not currently Done.
        var open = new List<AgingItem>();
        foreach (var i in issues)
        {
            var t = timelines[i.JiraIssueId];
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

        return new StatusAgingReport(
            new PagedResult<AgingItem>(items, page, pageSize, ordered.Count), histogram);
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
