using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// The value lists behind history conditions, from this project's changelog. Every read goes
/// through the injected tenant-scoped AppDbContext, so another tenant's rows are unreachable.
/// Grouped in the database and capped there; only the split of Jira's joined lists happens in
/// memory (with the separator the filter matches by), over at most <see cref="MaxRawValues"/>
/// distinct strings per side.
/// </summary>
public sealed class HistoryValueService : IHistoryValueService
{
    internal const int MaxFields = 200;
    internal const int MaxValues = 1000;

    /// <summary>The most frequent distinct raw strings read per side before splitting — bounds a field whose every value is unique.</summary>
    internal const int MaxRawValues = 5000;

    private readonly AppDbContext _db;
    public HistoryValueService(AppDbContext db) => _db = db;

    public async Task<HistoryFieldsResult> GetFieldsAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return new HistoryFieldsResult(null);

        var fields = await _db.IssueChangeHistory
            .Where(h => h.JiraProjectId == projectId)
            .GroupBy(h => h.Field)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name)
            .Take(MaxFields)
            .ToListAsync(ct);
        return new HistoryFieldsResult(fields.Select(f => new HistoryFieldCount(f.Name, f.Count)).ToList());
    }

    public async Task<HistoryValuesResult> GetValuesAsync(
        Guid projectId, string? field, string? kind, CancellationToken ct = default)
    {
        // Project first: an unknown or foreign project is a 404 whatever else the request says.
        if (!await _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct)) return new HistoryValuesResult(null, 0, null);
        if (string.IsNullOrWhiteSpace(field))
            return new HistoryValuesResult(null, 0, "'field' is required: the history field to list values for.");
        if (kind is not ("value" or "author"))
            return new HistoryValuesResult(null, 0, "'kind' must be 'value' or 'author'.");

        // The field is data, not a closed catalog: one no row uses simply lists nothing.
        var rows = _db.IssueChangeHistory.Where(h => h.JiraProjectId == projectId && h.Field == field);
        var groups = kind == "author"
            ? await GroupedAsync(rows.Select(h => h.Author), ct)
            : (await GroupedAsync(rows.Select(h => h.FromValue), ct))
                .Concat(await GroupedAsync(rows.Select(h => h.ToValue), ct)).ToList();

        var separator = IssueFilterTranslator.ListSeparatorFor(field);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var empty = 0;
        foreach (var (value, count) in groups)
        {
            if (string.IsNullOrEmpty(value)) { empty += count; continue; }
            // Authors are names; values may be Jira's joined lists, offered element by element —
            // exactly what the filter matches.
            var elements = kind == "author" ? new[] { value } : value.Split(separator);
            foreach (var element in elements.Where(e => e.Length > 0).Distinct(StringComparer.Ordinal))
                counts[element] = counts.GetValueOrDefault(element) + count;
        }

        var values = counts
            .Select(kv => new FilterValueCount(kv.Key, kv.Value))
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.Ordinal)
            .Take(MaxValues)
            .ToList();
        return new HistoryValuesResult(values, empty, null);
    }

    /// <summary>One side's distinct strings with their row counts, most frequent first, capped.</summary>
    private static async Task<List<(string? Value, int Count)>> GroupedAsync(IQueryable<string?> side, CancellationToken ct) =>
        (await side
            .GroupBy(v => v)
            .Select(g => new { Value = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count).ThenBy(x => x.Value)
            .Take(MaxRawValues)
            .ToListAsync(ct))
        .Select(x => (x.Value, x.Count))
        .ToList();
}
