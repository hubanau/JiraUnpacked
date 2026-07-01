using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private const int MaxRateLimitRetries = 3;
    private const int SearchPageSize = 100;

    private static readonly JsonSerializerOptions SearchJson = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<SearchPage> SearchIssuesPageAsync(
        JiraCredentials creds, string jql, string? nextPageToken, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = Build(HttpMethod.Post, creds, "/rest/api/3/search/jql");
            req.Content = JsonContent.Create(new
            {
                jql,
                maxResults = SearchPageSize,
                nextPageToken,
                expand = new[] { "changelog" }
            }, options: SearchJson);

            using var resp = await SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                await DelayForRetryAsync(resp, ct);
                continue;
            }
            EnsureOk(resp);

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

    private static async Task DelayForRetryAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var seconds = 1.0;
        if (resp.Headers.TryGetValues("Retry-After", out var vals) &&
            double.TryParse(vals.FirstOrDefault(), out var parsed))
            seconds = Math.Max(0, parsed);
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
    }
}
