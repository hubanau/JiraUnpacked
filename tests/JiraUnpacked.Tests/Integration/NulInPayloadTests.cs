// tests/JiraUnpacked.Tests/Integration/NulInPayloadTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Domain.Enums;
using JiraUnpacked.Infrastructure.Persistence;
using JiraUnpacked.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// A NUL in one issue's text used to end the whole sync.
///
/// PostgreSQL cannot store NUL in json or jsonb — jsonb becomes text and text cannot represent
/// it — so the INSERT for the page carrying that issue is rejected with 22P05. Jira accepts a NUL
/// pasted into a text field and serves it back escaped, so this is ordinary data, not corruption.
/// A real 42,918-issue run died on one.
///
/// This runs against real Postgres deliberately: the whole defect is a database constraint that
/// no in-memory provider has, and a stubbed store would pass while production failed.
/// </summary>
public sealed class NulInPayloadTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime, IDisposable
{
    private readonly JiraUnpackedApiFactory _factory;
    private readonly WireMockServer _jira = WireMockServer.Start();
    public NulInPayloadTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _jira.Stop();

    private sealed record ConnectionDto(Guid Id, string BaseUrl, string Email, DateTime CreatedAtUtc);
    private sealed record ProjectDto(Guid Id, Guid JiraConnectionId, string JiraProjectKey, string JiraProjectId, int SyncIntervalMinutes, DateTime CreatedAtUtc);

    [Fact]
    public async Task An_issue_whose_text_contains_a_NUL_still_syncs()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        // The summary carries a real NUL. Serialised to JSON it becomes the six-character escape
        // that Postgres refuses — the same shape as the issue that killed the production run.
        var withNul = "Date/Time of Incident: - June 22, 8:53 am" + (char)0 + " (reported)";

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new
        {
            issues = new object[] {
                new { id = "40001", key = "ACME-NUL",
                    fields = new { summary = withNul, created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } },
                // A second, ordinary issue on the same page: the rejected INSERT took the whole
                // batch with it, so a neighbour being lost is part of the defect.
                new { id = "40002", key = "ACME-OK",
                    fields = new { summary = "ordinary", created = "2024-01-01T00:00:00.000+0000", updated = "2024-01-02T00:00:00.000+0000", status = new { id = "10", name = "Done" } },
                    changelog = new { startAt = 0, maxResults = 100, total = 0, histories = new object[0] } }
            }
        }));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());

        var run = await db.SyncRuns.IgnoreQueryFilters().FirstAsync(r => r.JiraProjectId == project.Id);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);

        // Both issues stored and modelled — the NUL one is kept, not skipped: losing a real issue
        // over an invisible character would be its own defect.
        Assert.Equal(2, await db.RawIssueSnapshots.IgnoreQueryFilters().CountAsync(r => r.JiraProjectId == project.Id));
        Assert.Equal(2, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));

        // The text survives with only the NUL gone.
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "40001");
        Assert.Equal("Date/Time of Incident: - June 22, 8:53 am (reported)", issue.Summary);
        Assert.DoesNotContain('\0', issue.Summary!);
    }

    /// <summary>
    /// The second thing Postgres refuses that Jira happily serves: HALF a character.
    ///
    /// A code point above U+FFFF is written as two escapes that mean one character only together.
    /// One without its partner is not a character at all, and Postgres rejects the whole document
    /// with 22P02. It arrives from binary pasted into a text field — the run that found this died
    /// on an issue holding a raw PNG.
    ///
    /// The response is written as raw JSON rather than serialised from an object, because a lone
    /// surrogate cannot survive a round trip through a .NET string: encoding one to UTF-8 quietly
    /// substitutes U+FFFD, which would test the wrong thing entirely.
    /// </summary>
    [Fact]
    public async Task An_issue_carrying_half_a_character_still_syncs_and_emoji_survive()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        // The first summary carries an unpaired high half; the second a VALID pair (a rocket),
        // which must survive — emoji in summaries are ordinary, and stripping them would be a
        // worse bug than the one being fixed.
        //
        // Both are written as ESCAPES, not as literal characters. A literal rocket in this JSON
        // never reaches the pairing logic at all — it is just UTF-8 bytes the scanner copies — so
        // the test would pass even if valid pairs were being destroyed. A mutation check caught
        // exactly that: dropping paired surrogates left this test green until it was written
        // this way. In a C# raw string literal a backslash is literal, so these reach Jira's
        // stubbed response as the six-character escapes Jira itself sends.
        const string body = """
        {
          "issues": [
            { "id": "50001", "key": "ACME-HALF",
              "fields": { "summary": "PNG \ud83d IHDR", "created": "2024-01-01T00:00:00.000+0000", "updated": "2024-01-02T00:00:00.000+0000", "status": { "id": "10", "name": "Done" } },
              "changelog": { "startAt": 0, "maxResults": 100, "total": 0, "histories": [] } },
            { "id": "50002", "key": "ACME-EMOJI",
              "fields": { "summary": "ship it \ud83d\ude80", "created": "2024-01-01T00:00:00.000+0000", "updated": "2024-01-02T00:00:00.000+0000", "status": { "id": "10", "name": "Done" } },
              "changelog": { "startAt": 0, "maxResults": 100, "total": 0, "histories": [] } }
          ]
        }
        """;

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody(body));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());

        var run = await db.SyncRuns.IgnoreQueryFilters().FirstAsync(r => r.JiraProjectId == project.Id);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);

        Assert.Equal(2, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));

        // The orphan is gone; everything around it is untouched.
        var half = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "50001");
        Assert.Equal("PNG  IHDR", half.Summary);

        // And the rocket is still a rocket.
        var emoji = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "50002");
        Assert.Equal("ship it \U0001F680", emoji.Summary);
    }

    /// <summary>
    /// The bug this codebase INTRODUCED while fixing NUL — the one that actually killed PLAT.
    /// Jira escapes binary pasted into a text field, producing runs of escaped backslashes. Valid
    ///
    /// This is the one that actually killed the project's first sync, three times over, always at
    /// exactly one page. It survives the read because GetRawText hands back the ORIGINAL bytes
    /// without validating the escapes inside them — so the page parses perfectly and then the
    /// INSERT fails with 22P02, DETAIL "Escape sequence is invalid". Confirmed by turning on
    /// Postgres error detail against the real payload rather than by guessing, after two earlier
    /// theories about it — memory, then control characters — turned out to be wrong.
    ///
    /// The escape is written into the response body by hand: serialising it from an object would
    /// produce a VALID escape, which is precisely the case that already works.
    /// </summary>
    [Fact]
    public async Task An_issue_carrying_an_invalid_escape_still_syncs()
    {
        _jira.Given(Request.Create().WithPath("/rest/api/3/myself").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { accountId = "a" }));
        _jira.Given(Request.Create().WithPath("/rest/api/3/project/ACME").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBodyAsJson(new { id = "1", key = "ACME", name = "Acme" }));

        // Valid JSON: an ESCAPED backslash, then the literal characters u0000. Jira produces
        // this constantly for binary pasted into a text field, and Postgres accepts it.
        //
        // It is what the old blind substring replace could not tell from a real escaped NUL:
        // it matched from the SECOND backslash, cut six characters out, and left a lone
        // backslash in front of the next character. The stored payload was then invalid JSON
        // and Postgres refused it — a bug this codebase INTRODUCED while fixing NUL, which is
        // why the escape work now happens in a scanner that knows where each escape begins.
        var tricky = "PNG" + (char)0x5C + (char)0x5C + "u0000 IHDR";
        var body =
            "{\"issues\":[{\"id\":\"60001\",\"key\":\"ACME-ESC\",\"fields\":{\"summary\":\"" + tricky +
            "\",\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-02T00:00:00.000+0000\"," +
            "\"status\":{\"id\":\"10\",\"name\":\"Done\"}}," +
            "\"changelog\":{\"startAt\":0,\"maxResults\":100,\"total\":0,\"histories\":[]}}," +
            // An ordinary neighbour on the same page: the rejected INSERT took the whole batch
            // with it, so losing the innocent issue is part of the defect.
            "{\"id\":\"60002\",\"key\":\"ACME-FINE\",\"fields\":{\"summary\":\"ordinary\"," +
            "\"created\":\"2024-01-01T00:00:00.000+0000\",\"updated\":\"2024-01-02T00:00:00.000+0000\"," +
            "\"status\":{\"id\":\"10\",\"name\":\"Done\"}}," +
            "\"changelog\":{\"startAt\":0,\"maxResults\":100,\"total\":0,\"histories\":[]}}]}";

        _jira.Given(Request.Create().WithPath("/rest/api/3/search/jql").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody(body));

        var client = await TestData.AuthedClientAsync(_factory);
        var conn = (await (await client.PostAsJsonAsync("/api/connections", new { baseUrl = _jira.Url, email = "e@x.com", apiToken = "t" })).Content.ReadFromJsonAsync<ConnectionDto>())!;
        var project = (await (await client.PostAsJsonAsync($"/api/connections/{conn.Id}/projects", new { projectKey = "ACME", syncIntervalMinutes = 60 })).Content.ReadFromJsonAsync<ProjectDto>())!;

        var resp = await SyncWait.SyncAsync(client, project.Id);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(JiraUnpackedApiFactory.TestConnectionString).Options;
        await using var db = new AppDbContext(options, new NullCurrentTenant());

        var run = await db.SyncRuns.IgnoreQueryFilters().FirstAsync(r => r.JiraProjectId == project.Id);
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Null(run.Error);

        // Both issues land: the offending one is repaired, not skipped, and its neighbour is safe.
        Assert.Equal(2, await db.Issues.IgnoreQueryFilters().CountAsync(i => i.JiraProjectId == project.Id));

        // Only the stray backslash is gone — every character that was really there survives.
        var issue = await db.Issues.IgnoreQueryFilters().FirstAsync(i => i.JiraProjectId == project.Id && i.JiraIssueId == "60001");
        // Nothing is lost: the escaped backslash is still one backslash, and u0000 is still text.
        Assert.Equal("PNG" + (char)0x5C + "u0000 IHDR", issue.Summary);
    }
}
