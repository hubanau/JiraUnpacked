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
    public async Task A_filter_with_no_conditions_at_all_is_rejected()
    {
        var (client, projectId) = await SeedAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/filters", new { name = "Everything" });

        // Naming a filter that matches everything makes it look like a real constraint.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("must contain at least one condition", await resp.Content.ReadAsStringAsync());
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
}
