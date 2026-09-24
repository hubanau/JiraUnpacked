// tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JiraUnpacked.Domain.Abstractions;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// CRUD over named per-project filter definitions, plus the guards that keep a stored
/// definition honest. Tenant isolation for the new table is proven at the database in
/// TenantIsolationGateTests; what is asserted here is the API's behaviour on top of it.
/// </summary>
public sealed class SavedFilterTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SavedFilterTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// The API registers JsonStringEnumConverter globally, so FilterOperator/FilterLogic come
    /// back as names. HttpClient's default options do not, so the test needs its own.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static object Condition(string field, string op, object value) =>
        new { field, op, value };

    private static object Body(string name, params object[] conditions) =>
        new { name, conditions };

    private async Task<(HttpClient client, Guid projectId)> SeedAsync()
    {
        var (client, tenant) = await FilterTestData.AuthedTenantAsync(_factory);
        var projectId = await FilterTestData.SeedFilterFixtureAsync(tenant);
        return (client, projectId);
    }

    private static async Task<SavedFilterView> CreateOkAsync(HttpClient client, Guid projectId, object body)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters", body);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<SavedFilterView>(Json))!;
    }

    private static async Task<List<SavedFilterView>> ListAsync(HttpClient client, Guid projectId) =>
        (await client.GetFromJsonAsync<List<SavedFilterView>>($"/api/projects/{projectId}/filters", Json))!;

    /// <summary>POSTs a body the server must refuse and returns the ProblemDetails title.</summary>
    private static async Task<string> RejectedAsync(HttpClient client, Guid projectId, object body)
    {
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("title").GetString()!;
    }

    [Fact]
    public async Task Create_list_get_update_and_delete_round_trip()
    {
        var (client, projectId) = await SeedAsync();

        var created = await CreateOkAsync(client, projectId,
            Body("My open work", Condition("status", "Eq", "In Progress")));
        Assert.Equal("My open work", created.Name);
        Assert.Equal("In Progress", created.Conditions!.Single().Value!.Value.GetString());

        var list = await client.GetFromJsonAsync<List<SavedFilterView>>(
            $"/api/projects/{projectId}/filters", Json);
        Assert.Contains(list!, f => f.Id == created.Id && f.Name == "My open work");

        var fetched = await client.GetFromJsonAsync<SavedFilterView>(
            $"/api/projects/{projectId}/filters/{created.Id}", Json);
        Assert.Equal(created.Id, fetched!.Id);

        var updateResp = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/filters/{created.Id}",
            Body("Renamed", Condition("status", "Eq", "Done")));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = (await updateResp.Content.ReadFromJsonAsync<SavedFilterView>(Json))!;
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("Done", updated.Conditions!.Single().Value!.Value.GetString());
        Assert.Equal(created.CreatedAt, updated.CreatedAt);   // CreatedAt is not touched by an update

        var deleteResp = await client.DeleteAsync($"/api/projects/{projectId}/filters/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var gone = await client.GetAsync($"/api/projects/{projectId}/filters/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task A_nested_group_round_trips_and_is_stored_with_operator_NAMES_not_ordinals()
    {
        var (client, projectId) = await SeedAsync();

        var created = await CreateOkAsync(client, projectId, new
        {
            name = "Nested",
            filter = new
            {
                logic = "Or",
                conditions = new[] { new { field = "storyPoints", op = "Gte", value = 5 } },
                groups = new[]
                {
                    new
                    {
                        logic = "And",
                        conditions = new[]
                        {
                            new { field = "assignee", op = "Eq", value = (object)"Bob" },
                            new { field = "summary", op = "Contains", value = (object)"Child" },
                        },
                    },
                },
            },
        });

        Assert.Null(created.Conditions);
        Assert.Equal(FilterLogic.Or, created.Filter!.Logic);
        Assert.Equal(FilterOperator.Gte, created.Filter.Conditions!.Single().Op);

        var nested = Assert.Single(created.Filter.Groups!);
        Assert.Equal(FilterLogic.And, nested.Logic);
        Assert.Equal(2, nested.Conditions!.Count);

        // Read the stored jsonb directly. Ordinals would make FilterOperator's declaration
        // order load-bearing for data at rest: reordering the enum — which nothing forbids —
        // would silently reinterpret every saved filter. A typed assertion cannot see this,
        // because deserialisation would happily read the ordinals back.
        var stored = await ReadDefinitionAsync(created.Id);
        Assert.Contains("\"Gte\"", stored);
        Assert.Contains("\"Contains\"", stored);
        Assert.Contains("\"Or\"", stored);
        Assert.Contains("\"And\"", stored);
        Assert.DoesNotContain("\"op\":6", stored);
        Assert.DoesNotContain("\"logic\":1", stored);
    }

    private static async Task<string> ReadDefinitionAsync(Guid filterId)
    {
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT ""DefinitionJson""::text FROM ""SavedFilters"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", filterId);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task A_definition_the_query_would_reject_is_rejected_at_save_time()
    {
        var (client, projectId) = await SeedAsync();

        async Task<string> Rejected(object body)
        {
            var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters", body);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            return await resp.Content.ReadAsStringAsync();
        }

        // The same messages the query endpoint returns, because it is the same compiler —
        // a filter that cannot run is never stored and then discovered later.
        Assert.Contains("Unknown field 'secret'",
            await Rejected(Body("bad field", Condition("secret", "Eq", "x"))));

        Assert.Contains("is not allowed for field",
            await Rejected(Body("bad op", Condition("summary", "Gt", "x"))));

        Assert.Contains("at least one condition or nested group",
            await Rejected(new { name = "empty group", filter = new { logic = "And" } }));

        Assert.Contains("not both", await Rejected(new
        {
            name = "both",
            conditions = new[] { Condition("status", "Eq", "Done") },
            filter = new { logic = "Or", conditions = new[] { Condition("status", "Eq", "Done") } },
        }));
    }

    [Fact]
    public async Task A_view_that_stores_only_a_name_is_the_plain_list_and_is_allowed()
    {
        var (client, projectId) = await SeedAsync();

        // A view is a filter AND a presentation: "all issues, my columns" has no filter at all,
        // and the plain list itself is a legitimate thing to name (and to make the default).
        var created = await CreateOkAsync(client, projectId, new { name = "Everything" });

        Assert.Null(created.Filter);
        Assert.Null(created.Conditions);
        Assert.Null(created.Columns);
        Assert.Null(created.SortBy);
        Assert.Null(created.SortDescending);
    }

    [Fact]
    public async Task Name_is_required_trimmed_and_length_capped()
    {
        var (client, projectId) = await SeedAsync();
        var condition = Condition("status", "Eq", "Done");

        var blank = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters",
            Body("   ", condition));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("needs a name", await blank.Content.ReadAsStringAsync());

        var tooLong = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters",
            Body(new string('x', 101), condition));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("at most 100 characters", await tooLong.Content.ReadAsStringAsync());

        var trimmed = await CreateOkAsync(client, projectId, Body("  Padded  ", condition));
        Assert.Equal("Padded", trimmed.Name);
    }

    [Fact]
    public async Task Duplicate_name_in_the_same_project_is_409_but_the_same_name_elsewhere_is_fine()
    {
        var (client, projectId) = await SeedAsync();
        var (otherClient, otherProjectId) = await SeedAsync();
        var condition = Condition("status", "Eq", "Done");

        await CreateOkAsync(client, projectId, Body("Shared name", condition));

        var duplicate = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters",
            Body("Shared name", condition));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        // Uniqueness is per project, not global — another project may reuse the name.
        await CreateOkAsync(otherClient, otherProjectId, Body("Shared name", condition));
    }

    [Fact]
    public async Task The_unique_index_rejects_a_duplicate_name_at_the_database()
    {
        var (client, projectId) = await SeedAsync();
        var created = await CreateOkAsync(client, projectId,
            Body("Only one", Condition("status", "Eq", "Done")));

        // Deterministic half of the guarantee: the pre-check in the service is a courtesy, the
        // unique index is the real guard. Inserted through the privileged connection so no EF
        // filter or service check is in the path.
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO ""SavedFilters""(""Id"",""EndUserId"",""JiraProjectId"",""Name"",""DefinitionJson"",""CreatedAt"",""UpdatedAt"")
                            SELECT @id,""EndUserId"",@p,'Only one','{}'::jsonb,now(),now()
                            FROM ""SavedFilters"" WHERE ""Id"" = @existing";
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("existing", created.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23505", ex.SqlState);
    }

    [Fact]
    public async Task Concurrent_creates_of_the_same_name_yield_one_201_and_conflicts_never_a_500()
    {
        var (client, projectId) = await SeedAsync();
        var body = Body("Contended", Condition("status", "Eq", "Done"));

        // Opportunistic by nature — Task.WhenAll does not guarantee overlap, and if the calls
        // serialize the service's pre-check answers every loser. That is fine: what this pins
        // is that NO interleaving produces a 500. Before the 23505 catch, a lost race surfaced
        // as an unhandled DbUpdateException.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PostAsJsonAsync($"/api/projects/{projectId}/filters", body)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
    }

    [Fact]
    public async Task Renaming_a_filter_to_its_own_name_is_not_a_conflict_with_itself()
    {
        var (client, projectId) = await SeedAsync();
        var created = await CreateOkAsync(client, projectId,
            Body("Stable", Condition("status", "Eq", "Done")));

        var resp = await client.PutAsJsonAsync($"/api/projects/{projectId}/filters/{created.Id}",
            Body("Stable", Condition("status", "Eq", "In Progress")));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task An_oversized_definition_is_rejected()
    {
        var (client, projectId) = await SeedAsync();

        // Well inside the 20-condition guard, so this can only be caught by the size cap —
        // the translator bounds the NUMBER of conditions, never the LENGTH of a value.
        var huge = new string('a', 9000);
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/filters",
            Body("Huge", Condition("summary", "Contains", huge)));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("at most 8192 bytes", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_tenant_gets_404_on_every_route_and_never_the_row()
    {
        var (clientA, projectA) = await SeedAsync();
        var created = await CreateOkAsync(clientA, projectA,
            Body("A's filter", Condition("status", "Eq", "Done")));

        var (clientB, projectB) = await SeedAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{projectA}/filters")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.GetAsync($"/api/projects/{projectA}/filters/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.PutAsJsonAsync($"/api/projects/{projectA}/filters/{created.Id}",
                Body("hijack", Condition("status", "Eq", "Done")))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientB.DeleteAsync($"/api/projects/{projectA}/filters/{created.Id}")).StatusCode);

        // B's own project must not surface A's row either — a filtered-out row, not a 404 by
        // accident of the id.
        var bList = await clientB.GetFromJsonAsync<List<SavedFilterView>>(
            $"/api/projects/{projectB}/filters", Json);
        Assert.DoesNotContain(bList!, f => f.Id == created.Id);

        // And A still has it: B's four attempts changed nothing.
        var aList = await clientA.GetFromJsonAsync<List<SavedFilterView>>(
            $"/api/projects/{projectA}/filters", Json);
        Assert.Contains(aList!, f => f.Id == created.Id && f.Name == "A's filter");
    }

    [Fact]
    public async Task Deleting_the_owning_user_erases_the_saved_filter()
    {
        var (client, projectId) = await SeedAsync();
        var created = await CreateOkAsync(client, projectId,
            Body("Doomed", Condition("status", "Eq", "Done")));

        var deleted = await client.DeleteAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Proves the central ITenantOwned -> Users cascade in AppDbContext covered the
        // seventeenth tenant table without anyone adding a line for it.
        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT count(*) FROM ""SavedFilters"" WHERE ""Id"" = @id";
        cmd.Parameters.AddWithValue("id", created.Id);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Disabled_parts_are_saved_unvalidated_and_come_back_disabled()
    {
        var (client, projectId) = await SeedAsync();

        // The switched-off row names a field that does not exist. Validating it would make it
        // impossible to save a view with a broken row turned off — sub-project 2 depends on this.
        var created = await CreateOkAsync(client, projectId, new
        {
            name = "Partly off",
            filter = new
            {
                logic = "And",
                conditions = new object[]
                {
                    new { field = "status", op = "Eq", value = "Done" },
                    new { field = "nope", op = "Eq", value = "x", disabled = true },
                },
                groups = new[]
                {
                    new
                    {
                        logic = "Or",
                        disabled = true,
                        conditions = new[] { new { field = "assignee", op = "Eq", value = "Bob" } },
                    },
                },
            },
        });

        Assert.Null(created.Filter!.Conditions![0].Disabled);
        Assert.True(created.Filter.Conditions[1].Disabled);
        Assert.True(created.Filter.Groups!.Single().Disabled);
        // jsonb::text renders `"key": value` with a space after the colon.
        Assert.Contains("\"disabled\": true", await ReadDefinitionAsync(created.Id));
    }

    [Fact]
    public async Task Columns_and_sort_round_trip_next_to_a_filter_with_disabled_parts()
    {
        var (client, projectId) = await SeedAsync();

        var created = await CreateOkAsync(client, projectId, new
        {
            name = "My backlog",
            filter = new
            {
                logic = "And",
                conditions = new object[]
                {
                    new { field = "status", op = "Eq", value = "Done" },
                    new { field = "assignee", op = "Eq", value = "Bob", disabled = true },
                },
            },
            columns = new[] { "jiraKey", "summary", "priority" },
            sortBy = "priority",
            sortDescending = false,
        });

        Assert.Equal(new List<string> { "jiraKey", "summary", "priority" }, created.Columns);
        Assert.Equal("priority", created.SortBy);
        Assert.False(created.SortDescending);
        Assert.True(created.Filter!.Conditions![1].Disabled);

        var fetched = await client.GetFromJsonAsync<SavedFilterView>(
            $"/api/projects/{projectId}/filters/{created.Id}", Json);
        Assert.Equal(created.Columns, fetched!.Columns);
        Assert.Equal("priority", fetched.SortBy);
        Assert.False(fetched.SortDescending);

        var updateResp = await client.PutAsJsonAsync($"/api/projects/{projectId}/filters/{created.Id}", new
        {
            name = "My backlog",
            filter = new { logic = "And", conditions = new[] { new { field = "status", op = "Eq", value = "Done" } } },
            columns = new[] { "jiraKey", "team" },
            sortBy = "createdAt",
            sortDescending = true,
        });
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = (await updateResp.Content.ReadFromJsonAsync<SavedFilterView>(Json))!;
        Assert.Equal(new List<string> { "jiraKey", "team" }, updated.Columns);
        Assert.Equal("createdAt", updated.SortBy);
        Assert.True(updated.SortDescending);

        // Stored by name in the jsonb next to the filter, not in new columns.
        var stored = await ReadDefinitionAsync(created.Id);
        Assert.Contains("\"sortBy\": \"createdAt\"", stored);
        Assert.Contains("\"columns\": [\"jiraKey\", \"team\"]", stored);
    }

    [Fact]
    public async Task A_view_without_a_filter_keeps_its_columns_and_sort()
    {
        var (client, projectId) = await SeedAsync();

        var created = await CreateOkAsync(client, projectId, new
        {
            name = "Everything, by points",
            columns = new[] { "jiraKey", "storyPoints" },
            sortBy = "storyPoints",
        });

        Assert.Null(created.Filter);
        Assert.Equal(new List<string> { "jiraKey", "storyPoints" }, created.Columns);
        Assert.Equal("storyPoints", created.SortBy);
        // Absent means "the list's default", which is not the same claim as false.
        Assert.Null(created.SortDescending);
    }

    [Fact]
    public async Task Unknown_sort_fields_and_bad_column_lists_are_rejected()
    {
        var (client, projectId) = await SeedAsync();

        Assert.StartsWith("'secret' is not a sortable column",
            await RejectedAsync(client, projectId, new { name = "s", sortBy = "secret" }));
        // parent is a filter field and a column, but ordering by an opaque Jira id means nothing.
        Assert.StartsWith("'parent' is not a sortable column",
            await RejectedAsync(client, projectId, new { name = "p", sortBy = "parent" }));

        Assert.Equal("'secret' is not a known column.",
            await RejectedAsync(client, projectId, new { name = "c", columns = new[] { "jiraKey", "secret" } }));
        // Exact case: the SPA matches ids exactly, so "STATUS" would be stored and never shown.
        Assert.Equal("'STATUS' is not a known column.",
            await RejectedAsync(client, projectId, new { name = "u", columns = new[] { "STATUS" } }));
        // A JSON null inside the list is a bad column — a 400, not a 500 from a null dictionary key.
        Assert.Equal("'' is not a known column.",
            await RejectedAsync(client, projectId, new { name = "n", columns = new[] { "jiraKey", null } }));
        Assert.Equal("'status' is listed more than once.",
            await RejectedAsync(client, projectId, new { name = "d", columns = new[] { "status", "summary", "status" } }));
        Assert.Equal("A view may show at most 20 columns.",
            await RejectedAsync(client, projectId, new { name = "m", columns = Enumerable.Repeat("status", 21).ToArray() }));
    }
}
