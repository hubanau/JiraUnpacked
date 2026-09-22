using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private const int MaxTransientRetries = 3;
    private const int SearchPageSize = 100;

    /// <summary>
    /// A COMMA-DELIMITED STRING, not an array. /rest/api/3/search/jql is the exception Atlassian
    /// calls out in its own spec: "unlike the majority of instances where expand is specified,
    /// expand is defined as a comma-delimited string of values". Sending the array that every
    /// other endpoint takes is rejected with a 400 before the JQL is even looked at.
    /// </summary>
    private const string SearchExpand = "changelog";

    /// <summary>
    /// Required, and not an optimisation to trim later. This endpoint returns ONLY issue ids when
    /// `fields` is omitted — it differs from GET issue, whose default is every field — and
    /// DerivationService skips any issue whose payload carries no `fields` object, so the default
    /// would land raw rows that model into nothing at all.
    ///
    /// `*all` rather than a fixed list because field mappings point at arbitrary custom field ids
    /// (story points, sprint, team), which cannot be enumerated here.
    /// </summary>
    private static readonly string[] SearchFields = { "*all" };

    private static readonly JsonSerializerOptions SearchJson =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Progress reporting only, so every failure path returns null rather than throwing: a sync
    /// that works must not be turned into a failed one by the call that draws its progress bar.
    /// The count is Jira's own approximation and can disagree slightly with what paging returns,
    /// which is why the UI treats it as a denominator to display, never as a stopping condition.
    /// </summary>
    public async Task<int?> ApproximateCountAsync(
        JiraCredentials creds, string jql, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendWithRetryAsync(() =>
            {
                var req = Build(HttpMethod.Post, creds, "/rest/api/3/search/approximate-count");
                req.Content = JsonContent.Create(new { jql }, options: SearchJson);
                return req;
            }, ct);

            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct), JiraJson.Document);
            return doc.RootElement.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
                && c.TryGetInt32(out var count) ? count : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes JiraApiException from the retry helper. A cancellation is the caller
            // giving up and must still propagate.
            return null;
        }
    }

    public async Task<SearchPage> SearchIssuesPageAsync(
        JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
    {
        // Rate limits, 5xx and dropped connections are all retried by SendWithRetryAsync, which
        // rebuilds the request each attempt — necessary here because the body is content, and a
        // sent HttpRequestMessage cannot be reused.
        using var resp = await SendWithRetryAsync(() =>
        {
            var req = Build(HttpMethod.Post, creds, "/rest/api/3/search/jql");
            req.Content = JsonContent.Create(new
            {
                jql,
                maxResults = SearchPageSize,
                nextPageToken,
                fields = SearchFields,
                expand = SearchExpand
            }, options: SearchJson);
            return req;
        }, ct);

        await EnsureOkAsync(resp, ct);

        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct), JiraJson.Document);
            var root = doc.RootElement;

            var issues = new List<RawIssue>();
            if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    issues.Add(new RawIssue(el.GetProperty("id").GetString()!, el.GetRawText()));

            var token = root.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            return new SearchPage(issues, token);
        }
    }

    /// <summary>
    /// Jira's ceiling for a page that asks for ids only; pages carrying fields are capped far lower.
    /// </summary>
    private const int IdPageSize = 5000;

    private static readonly string[] IdFields = { "id" };

    public async Task<IssueIdPage> SearchIssueIdsPageAsync(
        JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() =>
        {
            var req = Build(HttpMethod.Post, creds, "/rest/api/3/search/jql");
            req.Content = JsonContent.Create(new
            {
                jql,
                maxResults = IdPageSize,
                nextPageToken,
                fields = IdFields,
            }, options: SearchJson);
            return req;
        }, ct);

        await EnsureOkAsync(resp, ct);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct), JiraJson.Document);
        var root = doc.RootElement;

        var ids = new List<string>();
        if (root.TryGetProperty("issues", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    ids.Add(id.GetString()!);

        var token = root.TryGetProperty("nextPageToken", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        return new IssueIdPage(ids, token);
    }

    public async Task<string?> GetUserTimeZoneAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendWithRetryAsync(() => Build(HttpMethod.Get, creds, "/rest/api/3/myself"), ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct), JiraJson.Document);
            return doc.RootElement.TryGetProperty("timeZone", out var tz) && tz.ValueKind == JsonValueKind.String
                ? tz.GetString()
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown is an answer the caller plans for (it widens the window instead); a failed
            // lookup must never fail the sync that asked.
            return null;
        }
    }
}
