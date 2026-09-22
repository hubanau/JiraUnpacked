using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions.Jira;

namespace JiraUnpacked.Infrastructure.Jira;

public sealed partial class JiraApiClient
{
    private static readonly JsonSerializerOptions Json = JiraJson.Web;

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
        await EnsureOkAsync(resp, ct);
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
        await EnsureOkAsync(resp, ct);
        var dto = (await resp.Content.ReadFromJsonAsync<JqlParseDto>(Json, ct))!;
        var errors = dto.Queries.FirstOrDefault()?.Errors ?? new List<string>();
        return new JqlValidationResult(errors.Count == 0, errors);
    }

    private async Task<T> GetAsync<T>(JiraCredentials creds, string path, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(() => Build(HttpMethod.Get, creds, path), ct);
        await EnsureOkAsync(resp, ct);
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
            // A cancellation the CALLER asked for is not a Jira fault and must surface as one.
            // Translating it into "Jira is unreachable" hid a deliberate Stop from the
            // orchestrator, which then recorded a failure through the already-cancelled token,
            // threw again, and left the run Running until the reaper — two hours of a blocked
            // project for pressing a button. A TaskCanceledException with ct NOT cancelled is
            // HttpClient's own timeout, which is transient and retried like any other.
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException)
                                       && !ct.IsCancellationRequested)
            {
                if (attempt >= MaxTransientRetries)
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

    private static async Task EnsureOkAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        // "Jira returned 400." is unactionable, and a 400 is the one status where Jira always
        // explains itself — a malformed request body or a rejected JQL both arrive this way, and
        // the two are indistinguishable without the body. Only the credential status stays
        // deliberately terse: nothing Jira says about an auth failure helps, and this message is
        // persisted to a tenant-readable SyncRun.Error.
        var detail = await ReadErrorDetailAsync(resp, ct);

        throw resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new JiraApiException(JiraErrorKind.Unauthorized, "Jira rejected the credentials."),
            HttpStatusCode.NotFound => new JiraApiException(JiraErrorKind.NotFound, "Jira resource not found."),
            HttpStatusCode.TooManyRequests => new JiraApiException(JiraErrorKind.RateLimited, "Jira rate limit hit."),
            _ => new JiraApiException(JiraErrorKind.Upstream,
                detail is null
                    ? $"Jira returned {(int)resp.StatusCode}."
                    : $"Jira returned {(int)resp.StatusCode}: {detail}")
        };
    }

    /// <summary>
    /// Jira's own words for a failure, as one bounded single-line string, or null if it said
    /// nothing usable. Never the request: the body we send carries the JQL, and the credentials
    /// live in the Authorization header, so nothing secret can arrive here — but the result is
    /// still capped, because it lands in a database column a user reads.
    /// </summary>
    private static async Task<string?> ReadErrorDetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        const int MaxDetailChars = 400;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;

            var messages = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(body, JiraJson.Document);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Jira uses errorMessages[] for request-level problems and errors{} for
                    // field-level ones; a malformed body can produce either.
                    if (doc.RootElement.TryGetProperty("errorMessages", out var arr)
                        && arr.ValueKind == JsonValueKind.Array)
                        foreach (var m in arr.EnumerateArray())
                            if (m.ValueKind == JsonValueKind.String) messages.Add(m.GetString()!);

                    if (doc.RootElement.TryGetProperty("errors", out var errs)
                        && errs.ValueKind == JsonValueKind.Object)
                        foreach (var p in errs.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.String)
                                messages.Add($"{p.Name}: {p.Value.GetString()}");
                }
            }
            catch (JsonException)
            {
                // Not JSON — a proxy or gateway in front of Jira. The raw text is still the
                // best clue available, so fall through to it rather than dropping it.
            }

            var detail = messages.Count > 0 ? string.Join("; ", messages) : body;
            detail = string.Join(" ", detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (detail.Length == 0) return null;
            return detail.Length <= MaxDetailChars ? detail : detail[..MaxDetailChars] + "…";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // The failure being reported matters more than a failure to read its explanation.
            return null;
        }
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
