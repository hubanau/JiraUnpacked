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
                expand = new[] { "changelog" }
            }, options: SearchJson);
            return req;
        }, ct);

        EnsureOk(resp);

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
