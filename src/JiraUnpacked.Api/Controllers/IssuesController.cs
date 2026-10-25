// src/JiraUnpacked.Api/Controllers/IssuesController.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JiraUnpacked.Api.Controllers;

[ApiController]
[Authorize]
public sealed class IssuesController : ControllerBase
{
    private readonly IIssueQueryService _issues;
    private readonly IFilterValueService _filterValues;
    private readonly IHistoryValueService _history;
    private readonly IIssueJqlService _jql;
    private readonly IIssueChartService _chart;

    public IssuesController(
        IIssueQueryService issues, IFilterValueService filterValues, IHistoryValueService history,
        IIssueJqlService jql, IIssueChartService chart)
    {
        _issues = issues;
        _filterValues = filterValues;
        _history = history;
        _jql = jql;
        _chart = chart;
    }

    [HttpGet("api/projects/{projectId:guid}/issues")]
    public async Task<IActionResult> List(Guid projectId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await _issues.ListAsync(projectId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("api/projects/{projectId:guid}/issues/{jiraIssueId}")]
    public async Task<IActionResult> Detail(Guid projectId, string jiraIssueId, CancellationToken ct = default)
    {
        var detail = await _issues.GetDetailAsync(projectId, jiraIssueId, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost("api/projects/{projectId:guid}/issues/query")]
    public async Task<IActionResult> Query(Guid projectId, IssueFilterRequest request, CancellationToken ct = default)
    {
        var result = await _issues.QueryAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.Result is null ? NotFound() : Ok(result.Result);
    }

    /// <summary>
    /// The list's filter as JQL, with a link that opens it in the project's Jira and every way the
    /// JQL differs from the filter. The same 404s and 400s as the query: the filter is checked by
    /// the same compiler first.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/issues/jql")]
    public async Task<IActionResult> Jql(Guid projectId, IssueJqlRequest request, CancellationToken ct = default)
    {
        var result = await _jql.BuildAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.View is null ? NotFound() : Ok(result.View);
    }

    /// <summary>
    /// The list's filtered issues grouped by one field, with counts and story points per group —
    /// the list's chart. The same 404s and 400s as the query: the filter is checked by the same
    /// compiler, and an unchartable field is a 400 naming the ones that can be charted.
    /// </summary>
    [HttpPost("api/projects/{projectId:guid}/issues/chart")]
    public async Task<IActionResult> Chart(Guid projectId, IssueChartRequest request, CancellationToken ct = default)
    {
        var result = await _chart.BuildAsync(projectId, request, ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        return result.Chart is null ? NotFound() : Ok(result.Chart);
    }

    /// <summary>
    /// The values a dictionary field actually takes in this project, for the filter's value
    /// picker. A field with no value list answers 400 naming it rather than an empty array:
    /// "this field has no picker" and "this project has no values" are different answers, and a
    /// client that cannot tell them apart shows the wrong empty state.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/filter-values")]
    public async Task<IActionResult> FilterValues(
        Guid projectId, [FromQuery] string? field, CancellationToken ct = default)
    {
        var result = await _filterValues.GetAsync(projectId, field ?? "", ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        if (result.Values is null) return NotFound();
        // `smart` only where it exists (sprint): its absence tells a client there is no Smart section.
        return result.Smart is null
            ? Ok(new { field, values = result.Values, emptyCount = result.EmptyCount })
            : Ok(new { field, values = result.Values, emptyCount = result.EmptyCount, smart = result.Smart });
    }

    /// <summary>The changelog fields this project has history for, most-changed first — the history row's field list.</summary>
    [HttpGet("api/projects/{projectId:guid}/history-fields")]
    public async Task<IActionResult> HistoryFields(Guid projectId, CancellationToken ct = default)
    {
        var result = await _history.GetFieldsAsync(projectId, ct);
        return result.Fields is null ? NotFound() : Ok(new { fields = result.Fields });
    }

    /// <summary>
    /// One history field's values (kind=value, the default: From and To, Jira's lists split into
    /// elements) or authors (kind=author), by name. An unknown field is an empty list, not a 400:
    /// fields are data here, not a closed catalog.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/history-values")]
    public async Task<IActionResult> HistoryValues(
        Guid projectId, [FromQuery] string? field, [FromQuery] string? kind, CancellationToken ct = default)
    {
        var result = await _history.GetValuesAsync(projectId, field, kind ?? "value", ct);
        if (result.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: result.Error);
        if (result.Values is null) return NotFound();
        return Ok(new { values = result.Values, emptyCount = result.EmptyCount });
    }

    /// <summary>Matches the SPA's own filter encoding, including its string enum names.</summary>
    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The filtered, sorted list as CSV. `filter` may be a boolean tree (a JSON object) or, for
    /// links made before groups existed, a flat `FilterCondition[]` joined by `logic`. A GET
    /// because the whole view already lives in the query string. Like every endpoint here it
    /// needs the bearer token, so the SPA fetches it rather than linking to it: a plain link is a
    /// navigation and carries no token.
    /// </summary>
    [HttpGet("api/projects/{projectId:guid}/issues/export.csv")]
    public async Task<IActionResult> Export(
        Guid projectId,
        [FromQuery] string? filter, [FromQuery] string? logic,
        [FromQuery] string? sortBy, [FromQuery] bool sortDescending = true,
        [FromQuery] string? cols = null,
        CancellationToken ct = default)
    {
        FilterGroup? group;
        try
        {
            group = ReadExportFilter(filter, logic);
        }
        catch (JsonException)
        {
            // A URL is user-editable and chat clients truncate them. Say so rather than 500.
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The filter in the link could not be read.");
        }

        var request = new IssueFilterRequest(null, group, 1, 1, sortBy, sortDescending);

        // Checked BEFORE a single byte is written: once the response has started there is no way
        // left to report a problem.
        var info = await _issues.PrepareExportAsync(projectId, request, ct);
        if (info.ProjectKey is null) return NotFound();
        if (info.Error is not null)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: info.Error);

        var columns = ExportColumns.Resolve(cols);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition =
            $"attachment; filename=\"{info.ProjectKey}-issues-{DateTime.UtcNow:yyyyMMdd}.csv\"";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(false));
        await writer.WriteAsync(CsvWriter.Row(columns.Select(c => c.Header)));

        // Written as they arrive. Buffering the set to build a string is the mistake this project
        // removed from derivation, and it is worse here: the response is held for the download.
        await foreach (var item in _issues.StreamExportAsync(projectId, request, ct))
            await writer.WriteAsync(CsvWriter.Row(columns.Select(c => c.Value(item))));

        return new EmptyResult();
    }

    /// <summary>
    /// `filter` in either shape a link may carry, told apart by its first character. A JSON
    /// object is the boolean tree the SPA writes now, carrying its own logic — so `logic` is
    /// ignored for it. A JSON array is the flat list links carried before groups existed,
    /// joined by `logic` exactly as before.
    /// </summary>
    private static FilterGroup? ReadExportFilter(string? filter, string? logic)
    {
        if (string.IsNullOrWhiteSpace(filter)) return null;

        if (filter.TrimStart().StartsWith('{'))
            return JsonSerializer.Deserialize<FilterGroup>(filter, FilterJson);

        var conditions = JsonSerializer.Deserialize<List<FilterCondition>>(filter, FilterJson);
        return conditions is { Count: > 0 }
            ? new FilterGroup(logic == "Or" ? FilterLogic.Or : FilterLogic.And, conditions, null)
            : null;
    }
}
