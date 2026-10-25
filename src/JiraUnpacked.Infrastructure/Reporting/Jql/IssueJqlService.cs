using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using Microsoft.EntityFrameworkCore;

namespace JiraUnpacked.Infrastructure.Reporting.Jql;

/// <summary>
/// The list's filter as JQL and a link that opens it in the project's Jira. Checked exactly as the
/// issue query checks it — project, then filter (by the shared compiler), then sort — so a filter
/// the list refuses is refused here with the same 400. Every id in the JQL comes from this
/// tenant's own rows, read through the injected tenant-scoped context; none from the request.
/// </summary>
public sealed class IssueJqlService : IIssueJqlService
{
    private readonly AppDbContext _db;
    private readonly IIssueFilterCompiler _compiler;
    private readonly JqlTranslator _translator;

    public IssueJqlService(AppDbContext db, IIssueFilterCompiler compiler, JqlTranslator translator)
    {
        _db = db;
        _compiler = compiler;
        _translator = translator;
    }

    public async Task<IssueJqlResult> BuildAsync(Guid projectId, IssueJqlRequest request, CancellationToken ct = default)
    {
        // Existence FIRST, as the query does: an unknown or another tenant's project is a 404 whatever the filter is.
        var project = await _db.JiraProjects.Where(p => p.Id == projectId)
            .Select(p => new { p.JiraProjectKey, p.JiraConnectionId })
            .FirstOrDefaultAsync(ct);
        if (project is null) return new IssueJqlResult(null, null);

        var compiled = await _compiler.CompileAsync(new ReportFilter(null, request.Filter), ct);
        if (compiled.Error is not null) return new IssueJqlResult(null, compiled.Error);
        if (!SortFields.IsKnown(request.SortBy)) return new IssueJqlResult(null, SortFields.Describe(request.SortBy));

        var baseUrl = await _db.JiraConnections.Where(c => c.Id == project.JiraConnectionId)
            .Select(c => c.BaseUrl).FirstOrDefaultAsync(ct) ?? "";
        var lookup = await LoadLookupAsync(projectId, project.JiraConnectionId, ct);

        var translation = _translator.Translate(
            project.JiraProjectKey, request.Filter, request.SortBy, request.SortDescending, lookup);
        var url = $"{baseUrl.TrimEnd('/')}/issues/?jql={Uri.EscapeDataString(translation.Jql)}";
        return new IssueJqlResult(
            new IssueJqlView(translation.Jql, url, translation.Warnings.Select(w => new JqlWarning(w)).ToList()),
            null);
    }

    /// <summary>
    /// What the translator needs, through the tenant-scoped context: the connection's users, the
    /// project's sprints and teams, and its Team and Story Points mappings. Small tables, read whole.
    /// </summary>
    private async Task<JqlLookup> LoadLookupAsync(Guid projectId, Guid connectionId, CancellationToken ct)
    {
        var users = await _db.JiraUsers.Where(u => u.JiraConnectionId == connectionId)
            .Select(u => new { u.DisplayName, u.JiraAccountId }).ToListAsync(ct);
        var sprints = await _db.Sprints.Where(s => s.JiraProjectId == projectId)
            .Select(s => new { s.Name, s.JiraSprintId }).ToListAsync(ct);
        var teams = await _db.Teams.Where(t => t.JiraProjectId == projectId)
            .Select(t => new { t.Name, t.JiraTeamId }).ToListAsync(ct);
        var mappings = await _db.FieldMappings.Where(m => m.JiraProjectId == projectId)
            .Select(m => new { m.Role, m.JiraFieldId }).ToListAsync(ct);

        return new JqlLookup(
            ByName(users.Select(u => (u.DisplayName, u.JiraAccountId))),
            ByName(sprints.Select(s => (s.Name, s.JiraSprintId))),
            ByName(teams.Select(t => (t.Name, t.JiraTeamId))),
            mappings.FirstOrDefault(m => m.Role == FieldRole.Team)?.JiraFieldId,
            mappings.FirstOrDefault(m => m.Role == FieldRole.StoryPoints)?.JiraFieldId);
    }

    /// <summary>
    /// Name → every id it has (a shared name keeps all of them). Names compare ordinally — the list's
    /// own resolver compares them with a plain SQL equality on a default (case-sensitive) column, so
    /// "alice" is not "Alice" in either. Ids are de-duplicated and ordered, so the JQL is stable.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ByName(IEnumerable<(string Name, string Id)> rows) =>
        rows.GroupBy(r => r.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Id).Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
}
