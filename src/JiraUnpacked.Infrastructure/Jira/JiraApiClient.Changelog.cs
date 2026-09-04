using System.Net;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private const int ChangelogPageSize = 100;

    public async Task<ChangelogPage> GetIssueChangelogPageAsync(
        JiraCredentials creds, string jiraIssueId, int startAt, CancellationToken ct = default)
    {
        var path = $"/rest/api/3/issue/{Uri.EscapeDataString(jiraIssueId)}/changelog?startAt={startAt}&maxResults={ChangelogPageSize}";
        using var resp = await SendWithRetryAsync(() => Build(HttpMethod.Get, creds, path), ct);
        await EnsureOkAsync(resp, ct);

        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var values = new List<string>();
            if (root.TryGetProperty("values", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    values.Add(el.GetRawText());

            var total = root.TryGetProperty("total", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : values.Count;
            var isLast = root.TryGetProperty("isLast", out var il) && il.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? il.GetBoolean()
                : startAt + values.Count >= total;

            return new ChangelogPage(values, total, isLast);
        }
    }
}
