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
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
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
}
