// src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace JiraUnpacked.Infrastructure.Reporting;

/// <summary>
/// CRUD over named per-project filter definitions. Every read goes through the injected
/// tenant-scoped AppDbContext (EF query filter + Postgres RLS).
///
/// Storage only: nothing here runs a filter. A definition is validated on the way IN through
/// the same compiler the query endpoints use, and compiled by that same compiler when a client
/// later posts it — so there is exactly one path from filter input to predicate, and writing
/// to this table directly reaches nothing new.
/// </summary>
public sealed class SavedFilterService : ISavedFilterService
{
    /// <summary>Per project. Enough for any real workflow, and bounds an unattended writer.</summary>
    public const int MaxPerProject = 100;

    /// <summary>
    /// The translator's guards bound the NUMBER of conditions but nothing bounds the LENGTH of
    /// a Contains value, so without this one filter could persist megabytes.
    /// </summary>
    public const int MaxDefinitionBytes = 8 * 1024;

    public const int MaxNameLength = 100;

    /// <summary>
    /// Every column once is 12 today; 20 leaves room for fields the catalog gains later without
    /// letting one definition grow without bound.
    /// </summary>
    public const int MaxColumns = 20;

    /// <summary>
    /// Enum values are written by NAME, never as ordinals. Storing ordinals would make
    /// FilterOperator's declaration order load-bearing for data at rest: reordering it — which
    /// nothing else forbids, and which FilterCatalogService already relies on for display —
    /// would silently reinterpret every saved filter into a different one.
    /// </summary>
    internal static readonly JsonSerializerOptions DefinitionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _filter;
    private readonly IClock _clock;

    public SavedFilterService(AppDbContext db, IIssueFilterCompiler filter, IClock clock)
    {
        _db = db;
        _filter = filter;
        _clock = clock;
    }

    /// <summary>
    /// Postgres timestamptz keeps microseconds; a .NET DateTime keeps 100ns ticks. Truncating
    /// before the write means the timestamp this service RETURNS on create is the one a later
    /// read gives back, instead of differing in the last digit.
    /// </summary>
    private static DateTime ToStoragePrecision(DateTime value) =>
        new(value.Ticks - (value.Ticks % 10), value.Kind);

    /// <summary>Tenant-scoped existence check: a cross-tenant/unknown project is filtered out => 404.</summary>
    private Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct) =>
        _db.JiraProjects.AnyAsync(p => p.Id == projectId, ct);

    public async Task<IReadOnlyList<SavedFilterView>?> ListAsync(Guid projectId, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;

        var rows = await _db.SavedFilters
            .Where(f => f.JiraProjectId == projectId)
            .OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id)
            .ToListAsync(ct);

        return rows.Select(ToView).ToList();
    }

    public async Task<SavedFilterView?> GetAsync(Guid projectId, Guid filterId, CancellationToken ct = default)
    {
        var row = await FindAsync(projectId, filterId, ct);
        return row is null ? null : ToView(row);
    }

    public async Task<SaveFilterResult> CreateAsync(
        Guid projectId, SaveFilterRequest request, CancellationToken ct = default)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return SaveFilterResult.NotFound();

        var (name, definition, failure) = await ValidateAsync(projectId, request, ct);
        if (failure is not null) return failure;

        if (await _db.SavedFilters.CountAsync(f => f.JiraProjectId == projectId, ct) >= MaxPerProject)
            return SaveFilterResult.Conflict($"A project may have at most {MaxPerProject} saved filters.");

        if (await NameTakenAsync(projectId, name!, null, ct))
            return SaveFilterResult.Conflict($"A saved filter named '{name}' already exists in this project.");

        var now = ToStoragePrecision(_clock.UtcNow);
        var row = new SavedFilter
        {
            Id = Guid.NewGuid(),
            JiraProjectId = projectId,
            Name = name!,
            DefinitionJson = definition!,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SavedFilters.Add(row);   // EndUserId is stamped by AppDbContext.SaveChanges.

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateName(ex))
        {
            return DuplicateName(name!);
        }

        return SaveFilterResult.Ok(ToView(row));
    }

    public async Task<SaveFilterResult> UpdateAsync(
        Guid projectId, Guid filterId, SaveFilterRequest request, CancellationToken ct = default)
    {
        var row = await FindAsync(projectId, filterId, ct);
        if (row is null) return SaveFilterResult.NotFound();

        var (name, definition, failure) = await ValidateAsync(projectId, request, ct);
        if (failure is not null) return failure;

        // Excludes this row, so re-saving under its own name is not a conflict with itself.
        if (await NameTakenAsync(projectId, name!, filterId, ct))
            return SaveFilterResult.Conflict($"A saved filter named '{name}' already exists in this project.");

        row.Name = name!;
        row.DefinitionJson = definition!;
        row.UpdatedAt = ToStoragePrecision(_clock.UtcNow);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateName(ex))
        {
            return DuplicateName(name!);
        }

        return SaveFilterResult.Ok(ToView(row));
    }

    public async Task<bool> DeleteAsync(Guid projectId, Guid filterId, CancellationToken ct = default)
    {
        var row = await FindAsync(projectId, filterId, ct);
        if (row is null) return false;

        _db.SavedFilters.Remove(row);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private Task<SavedFilter?> FindAsync(Guid projectId, Guid filterId, CancellationToken ct) =>
        _db.SavedFilters.FirstOrDefaultAsync(
            f => f.Id == filterId && f.JiraProjectId == projectId, ct);

    private static SaveFilterResult DuplicateName(string name) =>
        SaveFilterResult.Conflict($"A saved filter named '{name}' already exists in this project.");

    /// <summary>
    /// A concurrent write claimed the same name between the pre-check and this insert. The
    /// unique index on (JiraProjectId, Name) is the REAL guard; the pre-check only exists to
    /// give the common case a clean answer without provoking an exception. Mapping the race to
    /// the same 409 keeps a lost race from surfacing as a 500. Same shape as
    /// RegistrationService, JiraProjectService and SyncOrchestrator.
    /// </summary>
    private static bool IsDuplicateName(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };

    private Task<bool> NameTakenAsync(Guid projectId, string name, Guid? excluding, CancellationToken ct) =>
        _db.SavedFilters.AnyAsync(
            f => f.JiraProjectId == projectId && f.Name == name && (excluding == null || f.Id != excluding), ct);

    /// <summary>
    /// Name, presentation and definition checks. The filter goes through the SAME compiler the
    /// query endpoints use, so anything the query would reject is a 400 here rather than a
    /// surprise the next time someone opens the view. A view with no filter at all is fine: it
    /// is the plain list, perhaps with its own columns or sort.
    /// </summary>
    private async Task<(string? Name, string? Definition, SaveFilterResult? Failure)> ValidateAsync(
        Guid projectId, SaveFilterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return (null, null, SaveFilterResult.Invalid("A saved filter needs a name."));
        if (name.Length > MaxNameLength)
            return (null, null, SaveFilterResult.Invalid($"A name may be at most {MaxNameLength} characters."));

        if (!SortFields.IsKnown(request.SortBy))
            return (null, null, SaveFilterResult.Invalid(SortFields.Describe(request.SortBy)));

        if (DescribeBadColumns(request.Columns) is { } columnError)
            return (null, null, SaveFilterResult.Invalid(columnError));

        var compiled = await _filter.CompileAsync(request.ToFilter(), ct);
        if (compiled.Error is not null)
            return (null, null, SaveFilterResult.Invalid(compiled.Error));

        var definition = JsonSerializer.Serialize(
            new ViewDefinition(
                request.Conditions, request.Filter, request.Columns, request.SortBy, request.SortDescending),
            DefinitionJson);

        if (System.Text.Encoding.UTF8.GetByteCount(definition) > MaxDefinitionBytes)
            return (null, null, SaveFilterResult.Invalid(
                $"A saved filter definition may be at most {MaxDefinitionBytes} bytes."));

        return (name, definition, null);
    }

    /// <summary>
    /// Null when the list is acceptable. Column ids are the filter catalog's field names — one
    /// vocabulary for filtering, sorting and columns — matched EXACTLY: the catalog lookup ignores
    /// case, but the SPA does not, so "STATUS" would be stored and then never shown.
    /// </summary>
    private static string? DescribeBadColumns(List<string>? columns)
    {
        if (columns is null) return null;
        if (columns.Count > MaxColumns)
            return $"A view may show at most {MaxColumns} columns.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            // A JSON null inside the list arrives as a null string: a bad column, not a 500.
            if (column is null || !FilterFields.All.TryGetValue(column, out var field) || field.Name != column)
                return $"'{column}' is not a known column.";
            if (!seen.Add(column))
                return $"'{column}' is listed more than once.";
        }
        return null;
    }

    /// <summary>
    /// The column is jsonb and this service is the only writer, so Postgres has already
    /// validated the document and the shape is one this type round-trips. A definition written
    /// before views existed simply has no columns/sort, which reads back as null.
    /// </summary>
    private static SavedFilterView ToView(SavedFilter row)
    {
        var d = JsonSerializer.Deserialize<ViewDefinition>(row.DefinitionJson, DefinitionJson);
        return new SavedFilterView(
            row.Id, row.Name, d?.Conditions, d?.Filter, d?.Columns, d?.SortBy, d?.SortDescending,
            row.CreatedAt, row.UpdatedAt);
    }
}

/// <summary>
/// What SavedFilters.DefinitionJson holds: the filter every endpoint takes, plus the list's
/// presentation. Null members are omitted on write (DefinitionJson ignores nulls), so "absent"
/// and "not part of this view" are the same thing.
/// </summary>
internal sealed record ViewDefinition(
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns, string? SortBy, bool? SortDescending);
