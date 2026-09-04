// tests/JiraUnpacked.Tests/Integration/SyncWait.cs
using System.Net;
using System.Net.Http.Json;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Starts a run and waits for the background worker to finish it.
///
/// /sync and /rederive answer 202 and hand the work to SyncQueueWorker, so a test that posted
/// and then read the database immediately would be asserting against a run still in flight. The
/// alternative — swapping the queue for an inline double in tests — was rejected: it would mean
/// no test exercises the real controller-to-worker wiring, which is exactly the kind of gap that
/// let a malformed Jira request body pass a whole suite of stubbed tests.
///
/// The worker is in-process and test payloads are tiny, so completion is milliseconds; the
/// timeout is a deadlock guard, not a race margin. Waiting on the run's own terminal STATUS
/// rather than a sleep means this cannot pass by luck: a run that never finishes fails loudly.
/// </summary>
internal static class SyncWait
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private sealed record RunDto(Guid Id, string Status, string Kind, string? Error, int IssuesProcessed);

    public static Task<HttpResponseMessage> SyncAsync(HttpClient client, Guid projectId) =>
        StartAndWaitAsync(client, projectId, "sync");

    public static Task<HttpResponseMessage> RederiveAsync(HttpClient client, Guid projectId) =>
        StartAndWaitAsync(client, projectId, "rederive");

    private static async Task<HttpResponseMessage> StartAndWaitAsync(
        HttpClient client, Guid projectId, string action)
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{projectId}/{action}", new { });

        // 404/409 and rate-limit rejections never produce a run; hand them back for the caller
        // to assert on rather than waiting for something that will not arrive.
        if (response.StatusCode != HttpStatusCode.Accepted) return response;

        // Read the body as text and put an identical copy back. Reading it as JSON here would
        // consume the response stream, and callers that assert on the accepted run would then
        // fail with "Cannot access a closed Stream" — a confusing way to learn this helper had
        // already read it.
        var json = await response.Content.ReadAsStringAsync();
        response.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var started = System.Text.Json.JsonSerializer.Deserialize<RunDto>(
            json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        await WaitForRunAsync(client, projectId, started.Id);
        return response;
    }

    /// <summary>Polls until the named run leaves Running. Throws rather than returning on timeout.</summary>
    public static async Task WaitForRunAsync(HttpClient client, Guid projectId, Guid runId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var runs = await client.GetFromJsonAsync<List<RunDto>>($"/api/projects/{projectId}/sync-runs");
            var run = runs?.FirstOrDefault(r => r.Id == runId);
            if (run is not null && run.Status != "Running") return;
            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Run {runId} on project {projectId} was still Running after {Timeout.TotalSeconds:N0}s. " +
            "The background worker did not pick it up or did not finish it.");
    }

    /// <summary>Waits for every run on the project to settle — for tests that start several.</summary>
    public static async Task WaitForQuietAsync(HttpClient client, Guid projectId)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var runs = await client.GetFromJsonAsync<List<RunDto>>($"/api/projects/{projectId}/sync-runs");
            if (runs is not null && runs.All(r => r.Status != "Running")) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"Project {projectId} still had a Running run after {Timeout.TotalSeconds:N0}s.");
    }
}
