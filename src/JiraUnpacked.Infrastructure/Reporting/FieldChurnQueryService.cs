// src/JiraUnpacked.Infrastructure/Reporting/FieldChurnQueryService.cs
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// Field churn, computed on the fly from the ingested changelog — no Jira calls. Every read goes
/// through the injected tenant-scoped AppDbContext (EF query filter + Postgres RLS).
///
/// The count is deliberately NOT denormalised onto Issues. It is a pure aggregate of data already
/// stored, so a stored counter would be a second source of truth that has to be rebuilt in step
/// with an append-only, idempotent derivation; and a per-field column would bake one report's
/// question into the schema, with the next question needing the next column. Measured at
/// 1,000,000 changelog rows the grouped read is ~125ms on the existing
/// (EndUserId, JiraProjectId, Field, ChangedAt) index — cache it before denormalising it.
/// </summary>
public sealed class FieldChurnQueryService : IFieldChurnQueryService
{
    public const string DefaultField = "Sprint";

    private const int MaxPageSize = 100;
    private const int DefaultPageSize = 50;

    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _filter;

    public FieldChurnQueryService(AppDbContext db, IIssueFilterCompiler filter)
    {
        _db = db;
        _filter = filter;
    }

    public async Task<ReportResult<FieldChurnReport>> GetFieldChurnAsync(
        Guid projectId, string? field, int minChanges, IReadOnlyList<string>? statusClasses,
        int page, int pageSize, ReportFilter? filter = null, CancellationToken ct = default)
    {
        // Project first, so a bad filter aimed at another tenant's project still answers 404.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct))
            return ReportResult<FieldChurnReport>.NotFound();

        if (!TryParseClasses(statusClasses, out var classes, out var classError))
            return ReportResult<FieldChurnReport>.Invalid(classError!);

        var compiled = await _filter.CompileAsync(filter, ct);
        if (compiled.Error is not null) return ReportResult<FieldChurnReport>.Invalid(compiled.Error);

        field = string.IsNullOrWhiteSpace(field) ? DefaultField : field.Trim();
        minChanges = minChanges < 1 ? 1 : minChanges;
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        // Offered to the caller even when the chosen field has no changes, so a project that has
        // never moved a sprint can still be told what it DOES have history for.
        var availableFields = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId)
            .Select(h => h.Field)
            .Distinct()
            .OrderBy(f => f)
            .ToListAsync(ct);

        // Grouped in the DATABASE, not in memory: the changelog is the largest table here, and
        // only the issues that actually churned come back.
        var churn = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId && h.Field == field)
            .GroupBy(h => h.JiraIssueId)
            .Select(g => new
            {
                JiraIssueId = g.Key,
                Changes = g.Count(),
                DistinctValues = g.Select(x => x.ToValue).Distinct().Count(),
                FirstChangedAt = g.Min(x => x.ChangedAt),
                LastChangedAt = g.Max(x => x.ChangedAt),
            })
            .Where(x => x.Changes >= minChanges)
            .ToListAsync(ct);

        if (churn.Count == 0)
            return Empty(field, availableFields, page, pageSize);

        // Only the churned issues are fetched — the population is bounded by the aggregate above
        // rather than by the project's size.
        var churnedIds = churn.Select(c => c.JiraIssueId).ToList();
        var issues = await _db.Issues
            .Where(i => i.JiraProjectId == projectId && churnedIds.Contains(i.JiraIssueId))
            .Where(compiled.Predicate!)   // non-null whenever Error is null, checked above
            .Select(i => new
            {
                i.JiraIssueId,
                i.JiraKey,
                i.Summary,
                IssueType = _db.IssueTypes.Where(t => t.Id == i.IssueTypeId).Select(t => t.Name).FirstOrDefault(),
                Status = _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.Name).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var semantics = await LoadSemanticsAsync(projectId, ct);
        var byIssueId = churn.ToDictionary(c => c.JiraIssueId);

        var matched = new List<FieldChurnItem>();
        foreach (var i in issues)
        {
            // An unclassified status is Other, matching the rest of the semantic layer: absence of
            // a classification is a real state, not a reason to drop the row silently.
            var cls = i.Status is not null && semantics.TryGetValue(i.Status, out var c) ? c : StatusClass.Other;
            if (!classes.Contains(cls)) continue;

            var k = byIssueId[i.JiraIssueId];
            matched.Add(new FieldChurnItem(
                i.JiraIssueId, i.JiraKey, i.Summary, i.IssueType,
                i.Status, cls.ToString(), k.Changes, k.DistinctValues,
                k.FirstChangedAt, k.LastChangedAt));
        }

        // Histogram spans the whole matched population, deliberately computed before paging.
        var histogram = BuildHistogram(matched);

        var ordered = matched
            .OrderByDescending(m => m.Changes)
            .ThenByDescending(m => m.LastChangedAt)
            .ThenBy(m => m.JiraKey, StringComparer.Ordinal)
            .ToList();

        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return ReportResult<FieldChurnReport>.Ok(new FieldChurnReport(
            field, availableFields,
            new PagedResult<FieldChurnItem>(items, page, pageSize, ordered.Count),
            histogram));
    }

    private static ReportResult<FieldChurnReport> Empty(
        string field, IReadOnlyList<string> availableFields, int page, int pageSize) =>
        ReportResult<FieldChurnReport>.Ok(new FieldChurnReport(
            field, availableFields,
            new PagedResult<FieldChurnItem>(Array.Empty<FieldChurnItem>(), page, pageSize, 0),
            BuildHistogram(Array.Empty<FieldChurnItem>())));

    /// <summary>
    /// Fixed bands so the shape is comparable between projects and between runs. All five are
    /// always emitted, including the empty ones — a disappearing band reads as missing data.
    /// </summary>
    private static IReadOnlyList<FieldChurnBand> BuildHistogram(IReadOnlyList<FieldChurnItem> items)
    {
        var bands = new (string Name, int Lo, int Hi)[]
        {
            ("1", 1, 2), ("2", 2, 3), ("3", 3, 4), ("4", 4, 5), ("5+", 5, int.MaxValue),
        };

        return bands
            .Select(b => new FieldChurnBand(b.Name, items.Count(i => i.Changes >= b.Lo && i.Changes < b.Hi)))
            .ToList();
    }

    private static bool TryParseClasses(
        IReadOnlyList<string>? requested, out HashSet<StatusClass> classes, out string? error)
    {
        error = null;
        classes = new HashSet<StatusClass>();

        // The default is Active alone: the question is normally about work still in flight.
        if (requested is null || requested.Count == 0)
        {
            classes.Add(StatusClass.Active);
            return true;
        }

        foreach (var raw in requested)
        {
            if (!Enum.TryParse<StatusClass>(raw?.Trim(), ignoreCase: true, out var parsed))
            {
                classes = new HashSet<StatusClass>();
                error = $"Unknown status class '{raw}'. Expected any of: Active, Done, Other.";
                return false;
            }
            classes.Add(parsed);
        }
        return true;
    }

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
}
