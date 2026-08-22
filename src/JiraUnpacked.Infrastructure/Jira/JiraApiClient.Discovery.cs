using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<JiraField>> GetFieldsAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        var dto = await GetAsync<List<FieldDto>>(creds, "/rest/api/3/field", ct);
        return dto.Select(f => new JiraField(f.Id, f.Name, f.Custom)).ToList();
    }

    public async Task<IReadOnlyList<JiraProjectSummary>> GetProjectsAsync(JiraCredentials creds, CancellationToken ct = default)
    {
        var dto = await GetAsync<ProjectSearchDto>(creds, "/rest/api/3/project/search", ct);
        return dto.Values.Select(p => new JiraProjectSummary(p.Id, p.Key, p.Name)).ToList();
    }

    public async Task<JiraProjectSummary?> GetProjectAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(
            () => Build(HttpMethod.Get, creds, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        EnsureOk(resp);
        var p = (await resp.Content.ReadFromJsonAsync<ProjectDto>(Json, ct))!;
        return new JiraProjectSummary(p.Id, p.Key, p.Name);
    }

    public async Task<IReadOnlyList<JiraStatus>> GetStatusesAsync(JiraCredentials creds, string projectKey, CancellationToken ct = default)
    {
        var dto = await GetAsync<List<IssueTypeStatusesDto>>(
            creds, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}/statuses", ct);
        var byId = new Dictionary<string, JiraStatus>();
        foreach (var it in dto)
            foreach (var s in it.Statuses)
                byId.TryAdd(s.Id, new JiraStatus(s.Id, s.Name));
        return byId.Values.ToList();
    }

    public async Task<JqlValidationResult> ParseJqlAsync(JiraCredentials creds, string jql, CancellationToken ct = default)
    {
        using var resp = await SendWithRetryAsync(() =>
        {
            var req = Build(HttpMethod.Post, creds, "/rest/api/3/jql/parse");
            req.Content = JsonContent.Create(new { queries = new[] { jql } }, options: Json);
            return req;
        }, ct);
        EnsureOk(resp);
        var dto = (await resp.Content.ReadFromJsonAsync<JqlParseDto>(Json, ct))!;
        var errors = dto.Queries.FirstOrDefault()?.Errors ?? new List<string>();
        return new JqlValidationResult(errors.Count == 0, errors);
    }

    private async Task<T> GetAsync<T>(JiraCredentials creds, string path, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(() => Build(HttpMethod.Get, creds, path), ct);
        EnsureOk(resp);
        return (await resp.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    /// <summary>
    /// Sends, retrying transient failures: a rate limit, a 5xx, or a dropped connection. One
    /// blip should not fail a whole sync run.
    ///
    /// The request is REBUILT per attempt rather than reused — an HttpRequestMessage cannot be
    /// sent twice, and a retry that quietly reused one would throw on the second attempt
    /// instead of retrying. A caller-requested cancellation is never retried.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> build, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                using var req = build();
                resp = await _http.SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested || attempt >= MaxTransientRetries)
                    throw new JiraApiException(JiraErrorKind.Upstream, "Jira is unreachable.");

                await Task.Delay(Backoff(attempt), ct);
                continue;
            }

            if (IsTransient(resp) && attempt < MaxTransientRetries)
            {
                var delay = RetryAfter(resp) ?? Backoff(attempt);
                resp.Dispose();
                await Task.Delay(delay, ct);
                continue;
            }

            return resp;
        }
    }

    /// <summary>Rate limits and server-side faults are worth another attempt; a 4xx is not.</summary>
    private static bool IsTransient(HttpResponseMessage resp) =>
        resp.StatusCode == HttpStatusCode.TooManyRequests || (int)resp.StatusCode >= 500;

    /// <summary>Jira's own guidance when it sends one, in preference to any backoff we invent.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("Retry-After", out var values)
        && double.TryParse(values.FirstOrDefault(), out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 30))
            : null;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private static void EnsureOk(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new JiraApiException(JiraErrorKind.Unauthorized, "Jira rejected the credentials."),
            HttpStatusCode.NotFound => new JiraApiException(JiraErrorKind.NotFound, "Jira resource not found."),
            HttpStatusCode.TooManyRequests => new JiraApiException(JiraErrorKind.RateLimited, "Jira rate limit hit."),
            _ => new JiraApiException(JiraErrorKind.Upstream, $"Jira returned {(int)resp.StatusCode}.")
        };
    }

    private sealed record FieldDto([property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("custom")] bool Custom);
    private sealed record ProjectDto(string Id, string Key, string Name);
    private sealed record ProjectSearchDto(List<ProjectDto> Values);
    private sealed record IssueTypeStatusesDto(List<StatusDto> Statuses);
    private sealed record StatusDto(string Id, string Name);
    private sealed record JqlParseDto(List<JqlQueryDto> Queries);
    private sealed record JqlQueryDto(string Query, List<string> Errors);
}
