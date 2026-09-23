# Saved Views Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Set up the issue list once (filter, columns, sort), save it under a name, come back to it with one click, and let one view per project open automatically as the default.

**Architecture:** The existing, unused `SavedFilters` table (tenant-owned, RLS, FR-B2 case, CRUD at `/api/projects/{projectId}/filters`) becomes the view store: `DefinitionJson` gains optional `columns`/`sortBy`/`sortDescending`, and one migration adds `IsDefault` with a partial unique index. The SPA adds a toolbar `ViewsMenu` fed by a `useSavedViews` hook; a view is applied by writing its params into the URL through a new `useListState.applyParams` (push, or replace for the default), and "modified" compares what Save would write against what the view stores.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit 2.5; React 19, react-router-dom 7, @tanstack/react-query 5, Vitest + Testing Library + user-event 14 + MSW 2, Tailwind 4.

**Spec:** `docs/superpowers/specs/2026-09-23-saved-views-design.md`

## Global Constraints

- **Work only in the worktree `C:/jw-filters`** (branch `feat/saved-views`, built on `feat/filter-builder-v2`). Never read or write `C:\Users\AliaksandrHubanau\Private\Claude\JiraUnpacked` — other sessions use that checkout. Do not push.
- **Integration tests use their own database:** every `dotnet test` run sets `JU_TEST_DATABASE=ju_test_views` (Postgres = the `jiraunpacked-db` container on `localhost:5432`, already running). Bash: `JU_TEST_DATABASE=ju_test_views dotnet test …`. PowerShell: `$env:JU_TEST_DATABASE='ju_test_views'; dotnet test …`. The suite never truncates; a shared database collides with parallel branches.
- **Web setup:** `node_modules` already exists in `src/JiraUnpacked.Web`; if it is missing, run `npm ci` there once.
- SPA keeps **exactly four runtime deps**: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **No new dependencies of any kind** (runtime or dev).
- The HTTP route stays `/api/projects/{projectId}/filters[/{filterId}]`; the entity/table stay `SavedFilter`/`SavedFilters`. The SPA calls the thing a *view* (`api/views.ts`, `SavedView`).
- **One migration**, generated (never hand-written) with `dotnet ef migrations add AddSavedFilterDefault -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api -o Persistence/Migrations`: column `IsDefault boolean NOT NULL DEFAULT false` + partial unique index `IX_SavedFilters_OneDefaultPerProject` on `(JiraProjectId) WHERE "IsDefault"` (`HasIndex(...).IsUnique().HasFilter("\"IsDefault\"")`). RLS policy unchanged. `dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api` must report no changes.
- All reads/writes through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no `FromSql`.
- Server validation (400 with a message): `sortBy` must satisfy `SortFields.IsKnown`; `columns` at most **20**, each an exact field name from `FilterFields.All`, no duplicates; `filter` goes through `IIssueFilterCompiler` (disabled parts allowed). Name required, trimmed, ≤ **100**; unique per project (409); max **100** views per project (409); definition ≤ **8 KiB**.
- `IsDefault`: null on create = false; null on update = unchanged; `true` clears the project's other default **first** (`ExecuteUpdateAsync` in the same transaction); `false` just clears it. A race that trips the index is a 409, never a 500.
- List order: default first, then name case-insensitive.
- URL: a loaded view is `view=<id>`. Applying a view **pushes**; the default view on a bare URL **replaces**. Defaults are written as absent params, exactly as `useListState` writes them. A bare URL = none of `filter`, `logic`, `sortBy`, `sortDesc`, `cols`, `view`.
- UI copy, verbatim: `Views` · `Views: <name>` · ` • modified` · `Save` · `Save as new view…` · `Rename…` · `Set as default` · `Remove default` · `Delete…` · `A view with this name already exists` · `Views unavailable`. ★ marks the default.
- `src/ui/` imports neither `src/api/` nor `src/auth/`; new components/hooks live in `src/features/issues/`. Colours only through `--ju-*` CSS variables.
- Only `ju.username` and `ju.theme` may go to `localStorage`. List state lives in the URL; views live on the server.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (second `-m`).

## Review Focus

1. **A view stored by something other than this SPA** — a flat `conditions` list, explicit JSON nulls (`"value": null`, `"groups": null`) as the API serialises them, no columns/sort at all. Expected to open as the same list and not read as modified. Pinned by Task 4 (`opens a view the server returned with explicit nulls…`, `opens a flat conditions list…`, `treats a view that stores nothing as the default list`).
2. **Rows written before the migration, or by inserts that do not name `IsDefault`** (the FR-B2 gate, lifecycle fixtures) — expected to be non-default and to coexist without tripping the index. Pinned by Task 2 (`The_database_allows_at_most_one_default_view_per_project` inserts a row without the column) and Task 2's gate-test run.
3. **Two tabs making different views the default at the same moment** — expected: exactly one default afterwards, each request a 201 or a 409, never a 500. Pinned by Task 2 (`Concurrent_default_claims_end_with_exactly_one_default_and_never_a_500`).
4. **A hand-built `columns` list with a JSON `null` or the wrong case (`"STATUS"`)** — expected a 400 naming the column, not a 500 from a null dictionary key and not a stored view whose columns never show. Pinned by Task 1 (`Unknown_sort_fields_and_bad_column_lists_are_rejected`).
5. **Saving while an unfinished row or an is/is not (Empty) row is on screen** — expected: the server accepts it (unfinished rows dropped, (Empty) sent as is empty / is not empty) and the `• modified` marker clears after Save. Pinned by Task 4 (`writes (Empty) on is / is not…`, `ignores a row that is still being filled in`) and Task 5 (`Save overwrites the loaded view…` asserts the marker clears).

## Decisions this plan makes where the spec is silent

- **A view may store no filter.** `SavedFilterService` drops its "must contain at least one condition" rule: "all issues, my columns" and the plain list itself are legitimate views. An empty *group* is still a 400 (compiler).
- **"Modified" compares savable forms.** Both sides go through `toSavable` (unfinished rows and emptied groups out, disabled parts kept) plus the URL's normalisation (defaults ≡ absent, columns in table order). So a half-typed row is not a change, and Save always clears the marker.
- **Columns are matched exactly** against `FilterFields` names (the catalog lookup is case-insensitive, the SPA is not). The SPA sends columns in table order (`shownColumns`).
- **Rename and Set/Remove default re-send the view's stored definition**, not the list's current edits (PUT replaces the definition); only the toolbar `Save` writes the current list.
- **Where errors show:** menu actions → inside the menu (it stays open, input intact); toolbar `Save` → an alert under the toolbar; delete → inside the `ConfirmDialog`. A 409 whose title says "already exists" shows `A view with this name already exists`; any other 409 (the per-project cap) shows the server's title. A lost default race gets its own 409 message.
- **Every successful menu action closes the menu.** When the views list failed to load, the menu shows only `Views unavailable` (no actions).
- **The default view yields** to any list state that appears before the views arrive, and is decided once per mount.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/JiraUnpacked.Domain/Abstractions/ISavedFilterService.cs` | `SavedFilterView` + `SaveFilterRequest` gain `Columns`, `SortBy`, `SortDescending`, `IsDefault` |
| `src/JiraUnpacked.Domain/Entities/SavedFilter.cs` | + `IsDefault` |
| `src/JiraUnpacked.Infrastructure/Persistence/Configurations/SavedFilterConfiguration.cs` | partial unique index `IX_SavedFilters_OneDefaultPerProject` |
| `src/JiraUnpacked.Infrastructure/Persistence/Migrations/*_AddSavedFilterDefault.cs` | **generated** — column + index |
| `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs` | `ViewDefinition` jsonb shape, column/sort validation, default handling in one transaction, list order |
| `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs` | round trips, validation, default rules, index, race, order |
| `src/JiraUnpacked.Web/src/features/issues/useListState.ts` | + `LIST_PARAMS`, `ListParams`, `viewId`, `hasListState`, `setViewId`, `applyParams` |
| `src/JiraUnpacked.Web/src/features/issues/filterTree.ts` | + `toSavable` |
| `src/JiraUnpacked.Web/src/api/views.ts` | **new** — `SavedView`, `SaveViewRequest`, list/create/update/delete |
| `src/JiraUnpacked.Web/src/api/queryKeys.ts` | + `views(projectId)` |
| `src/JiraUnpacked.Web/src/features/issues/viewState.ts` | **new** — pure view model: `viewTree`, `viewParams`, `isModified`, `saveRequest`, `resaveRequest`, `shownColumns`, cache helpers |
| `src/JiraUnpacked.Web/src/features/issues/useSavedViews.ts` | **new** — views query, loaded view, modified, apply; default view + stale id handling |
| `src/JiraUnpacked.Web/src/features/issues/ViewsMenu.tsx` | **new** — toolbar button, Save, popover menu, name form, delete confirm |
| `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx` | wires the menu, page reset on apply, query gate |
| `src/JiraUnpacked.Web/src/test/viewsFixture.ts` | **new** — `noSavedViews`, `savedView`, stateful `mockViewsApi` |
| `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx` | **new** — IssueList + views end to end |

---

### Task 1: Columns and sort in a saved view (server)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/ISavedFilterService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`

**Interfaces:**
- Produces: `SavedFilterView(Guid Id, string Name, List<FilterCondition>? Conditions, FilterGroup? Filter, List<string>? Columns, string? SortBy, bool? SortDescending, DateTime CreatedAt, DateTime UpdatedAt)` — JSON `id, name, conditions, filter, columns, sortBy, sortDescending, createdAt, updatedAt`.
- Produces: `SaveFilterRequest(string? Name, List<FilterCondition>? Conditions, FilterGroup? Filter, List<string>? Columns = null, string? SortBy = null, bool? SortDescending = null)`.
- Produces: `SavedFilterService.MaxColumns = 20`; `internal sealed record ViewDefinition(List<FilterCondition>? Conditions, FilterGroup? Filter, List<string>? Columns, string? SortBy, bool? SortDescending)` — the `DefinitionJson` shape.
- 400 titles: `SortFields.Describe(sortBy)` (`"'x' is not a sortable column. Sortable: …"`), `"A view may show at most 20 columns."`, `"'x' is not a known column."`, `"'x' is listed more than once."`.

- [ ] **Step 1: Write the failing tests**

In `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`, add these helpers right after `CreateOkAsync`:

```csharp
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
```

**Replace** the test `A_filter_with_no_conditions_at_all_is_rejected` with:

```csharp
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
```

Append these tests to the class:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_views dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SavedFilterTests" -v q`
Expected: build FAILS — `SavedFilterView` has no `Columns`/`SortBy`/`SortDescending`.

- [ ] **Step 3: Extend the records**

In `src/JiraUnpacked.Domain/Abstractions/ISavedFilterService.cs`, replace the two records at the top with:

```csharp
/// <summary>
/// One saved view: the conditions/filter pair every other endpoint takes, plus the list's
/// presentation. A member the view does not store comes back null — "use the list's default".
/// </summary>
public sealed record SavedFilterView(
    Guid Id, string Name,
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns, string? SortBy, bool? SortDescending,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Body of the create and update calls. Columns/SortBy/SortDescending are the list's
/// presentation; left out, the view stores nothing for them and the list uses its defaults.
/// </summary>
public sealed record SaveFilterRequest(
    string? Name, List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns = null, string? SortBy = null, bool? SortDescending = null)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}
```

- [ ] **Step 4: Store and validate the presentation**

In `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs`:

(a) Below `public const int MaxNameLength = 100;` add:

```csharp
    /// <summary>
    /// Every column once is 12 today; 20 leaves room for fields the catalog gains later without
    /// letting one definition grow without bound.
    /// </summary>
    public const int MaxColumns = 20;
```

(b) Replace the whole `ValidateAsync` method (doc comment included) with:

```csharp
    /// <summary>
    /// Name, presentation and definition checks. The filter goes through the SAME compiler the
    /// query endpoints use, so anything the query would reject is a 400 here rather than a
    /// surprise the next time someone opens the view. A view with no filter at all is fine: it
    /// is the plain list, perhaps with its own columns or sort.
    /// </summary>
    private async Task<(string? Name, string? Definition, SaveFilterResult? Failure)> ValidateAsync(
        Guid projectId, SaveFilterRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return (null, null, SaveFilterResult.Invalid("A saved filter needs a name."));
        if (name.Length > MaxNameLength)
            return (null, null, SaveFilterResult.Invalid($"A name may be at most {MaxNameLength} characters."));

        if (!SortFields.IsKnown(request.SortBy))
            return (null, null, SaveFilterResult.Invalid(SortFields.Describe(request.SortBy)));

        if (DescribeBadColumns(request.Columns) is { } columnError)
            return (null, null, SaveFilterResult.Invalid(columnError));

        var compiled = await _filter.CompileAsync(request.ToFilter(), ct);
        if (compiled.Error is not null)
            return (null, null, SaveFilterResult.Invalid(compiled.Error));

        var definition = JsonSerializer.Serialize(
            new ViewDefinition(
                request.Conditions, request.Filter, request.Columns, request.SortBy, request.SortDescending),
            DefinitionJson);

        if (System.Text.Encoding.UTF8.GetByteCount(definition) > MaxDefinitionBytes)
            return (null, null, SaveFilterResult.Invalid(
                $"A saved filter definition may be at most {MaxDefinitionBytes} bytes."));

        return (name, definition, null);
    }

    /// <summary>
    /// Null when the list is acceptable. Column ids are the filter catalog's field names — one
    /// vocabulary for filtering, sorting and columns — matched EXACTLY: the catalog lookup ignores
    /// case, but the SPA does not, so "STATUS" would be stored and then never shown.
    /// </summary>
    private static string? DescribeBadColumns(List<string>? columns)
    {
        if (columns is null) return null;
        if (columns.Count > MaxColumns)
            return $"A view may show at most {MaxColumns} columns.";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            // A JSON null inside the list arrives as a null string: a bad column, not a 500.
            if (column is null || !FilterFields.All.TryGetValue(column, out var field) || field.Name != column)
                return $"'{column}' is not a known column.";
            if (!seen.Add(column))
                return $"'{column}' is listed more than once.";
        }
        return null;
    }
```

(c) Replace `ToView` with:

```csharp
    /// <summary>
    /// The column is jsonb and this service is the only writer, so Postgres has already
    /// validated the document and the shape is one this type round-trips. A definition written
    /// before views existed simply has no columns/sort, which reads back as null.
    /// </summary>
    private static SavedFilterView ToView(SavedFilter row)
    {
        var d = JsonSerializer.Deserialize<ViewDefinition>(row.DefinitionJson, DefinitionJson);
        return new SavedFilterView(
            row.Id, row.Name, d?.Conditions, d?.Filter, d?.Columns, d?.SortBy, d?.SortDescending,
            row.CreatedAt, row.UpdatedAt);
    }
```

(d) At the end of the file (after the class, inside the namespace), add:

```csharp
/// <summary>
/// What SavedFilters.DefinitionJson holds: the filter every endpoint takes, plus the list's
/// presentation. Null members are omitted on write (DefinitionJson ignores nulls), so "absent"
/// and "not part of this view" are the same thing.
/// </summary>
internal sealed record ViewDefinition(
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns, string? SortBy, bool? SortDescending);
```

`SortFields` and `FilterFields` are `internal` in `JiraUnpacked.Infrastructure.Reporting.Filtering`, which this file already imports.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_views dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SavedFilterTests" -v q`
Expected: PASS (all existing `SavedFilterTests` included — their bodies send no columns/sort).

- [ ] **Step 6: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/ISavedFilterService.cs src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs
git commit -m "feat(views): store columns and sort in a saved view" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: One default view per project (server + migration)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Entities/SavedFilter.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/ISavedFilterService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Persistence/Configurations/SavedFilterConfiguration.cs`
- Create (generated): `src/JiraUnpacked.Infrastructure/Persistence/Migrations/<timestamp>_AddSavedFilterDefault.cs` + `.Designer.cs`; modify (generated) `AppDbContextModelSnapshot.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs`
- Test: `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`

**Interfaces:**
- Consumes: Task 1's records and `ViewDefinition`.
- Produces: `SavedFilterView(Guid Id, string Name, List<FilterCondition>? Conditions, FilterGroup? Filter, List<string>? Columns, string? SortBy, bool? SortDescending, bool IsDefault, DateTime CreatedAt, DateTime UpdatedAt)` — JSON gains `isDefault`.
- Produces: `SaveFilterRequest(..., bool? SortDescending = null, bool? IsDefault = null)`.
- Produces: `SavedFilterConfiguration.OneDefaultIndex = "IX_SavedFilters_OneDefaultPerProject"`.
- Produces: `GET /filters` ordered default first, then `lower(Name)`, then `Id`.

- [ ] **Step 1: Write the failing tests**

Append to `SavedFilterTests`:

```csharp
    [Fact]
    public async Task Making_a_view_the_default_clears_the_previous_default()
    {
        var (client, projectId) = await SeedAsync();

        var a = await CreateOkAsync(client, projectId, new { name = "A", isDefault = true });
        Assert.True(a.IsDefault);

        var b = await CreateOkAsync(client, projectId, new { name = "B", isDefault = true });
        Assert.Equal(b.Id, Assert.Single(await ListAsync(client, projectId), v => v.IsDefault).Id);

        var resp = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/filters/{a.Id}", new { name = "A", isDefault = true });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(a.Id, Assert.Single(await ListAsync(client, projectId), v => v.IsDefault).Id);
    }

    [Fact]
    public async Task IsDefault_left_out_keeps_the_flag_on_update_and_means_false_on_create()
    {
        var (client, projectId) = await SeedAsync();

        var plain = await CreateOkAsync(client, projectId, new { name = "Plain" });
        Assert.False(plain.IsDefault);

        var a = await CreateOkAsync(client, projectId, new { name = "A", isDefault = true });

        // Renaming a view must never silently un-default it.
        var renamed = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/filters/{a.Id}", new { name = "A renamed" });
        Assert.True((await renamed.Content.ReadFromJsonAsync<SavedFilterView>(Json))!.IsDefault);

        var cleared = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/filters/{a.Id}", new { name = "A renamed", isDefault = false });
        Assert.False((await cleared.Content.ReadFromJsonAsync<SavedFilterView>(Json))!.IsDefault);
        Assert.DoesNotContain(await ListAsync(client, projectId), v => v.IsDefault);
    }

    [Fact]
    public async Task The_list_puts_the_default_first_then_orders_by_name_ignoring_case()
    {
        var (client, projectId) = await SeedAsync();
        await CreateOkAsync(client, projectId, new { name = "Banana" });
        await CreateOkAsync(client, projectId, new { name = "apple" });
        await CreateOkAsync(client, projectId, new { name = "Cherry", isDefault = true });

        // Ordinal order would put "Banana" before "apple".
        Assert.Equal(
            new List<string> { "Cherry", "apple", "Banana" },
            (await ListAsync(client, projectId)).Select(v => v.Name).ToList());
    }

    [Fact]
    public async Task The_database_allows_at_most_one_default_view_per_project()
    {
        var (client, projectId) = await SeedAsync();
        var first = await CreateOkAsync(client, projectId, new { name = "First", isDefault = true });

        await using var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString);
        await conn.OpenAsync();

        // Copies EndUserId/JiraProjectId from the row the API wrote, through the privileged
        // connection, so no service check is in the path — this is the index alone.
        async Task InsertAsync(string name, bool? isDefault)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = isDefault is null
                ? @"INSERT INTO ""SavedFilters""(""Id"",""EndUserId"",""JiraProjectId"",""Name"",""DefinitionJson"",""CreatedAt"",""UpdatedAt"")
                    SELECT @id,""EndUserId"",""JiraProjectId"",@name,'{}'::jsonb,now(),now()
                    FROM ""SavedFilters"" WHERE ""Id"" = @existing"
                : @"INSERT INTO ""SavedFilters""(""Id"",""EndUserId"",""JiraProjectId"",""Name"",""DefinitionJson"",""CreatedAt"",""UpdatedAt"",""IsDefault"")
                    SELECT @id,""EndUserId"",""JiraProjectId"",@name,'{}'::jsonb,now(),now(),@isDefault
                    FROM ""SavedFilters"" WHERE ""Id"" = @existing";
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("existing", first.Id);
            if (isDefault is { } flag) cmd.Parameters.AddWithValue("isDefault", flag);
            await cmd.ExecuteNonQueryAsync();
        }

        // An insert that does not name the column (every writer that predates it, including the
        // FR-B2 gate) gets false, and any number of non-defaults coexist: the index covers only
        // rows with the flag set.
        await InsertAsync("Written before the column", null);
        await InsertAsync("Plain", false);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync("Second default", true));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal("IX_SavedFilters_OneDefaultPerProject", ex.ConstraintName);

        var list = await ListAsync(client, projectId);
        Assert.Equal(first.Id, Assert.Single(list, v => v.IsDefault).Id);
        Assert.Contains(list, v => v.Name == "Written before the column" && !v.IsDefault);
    }

    [Fact]
    public async Task Concurrent_default_claims_end_with_exactly_one_default_and_never_a_500()
    {
        var (client, projectId) = await SeedAsync();

        // Opportunistic: Task.WhenAll does not guarantee overlap. What it pins is that NO
        // interleaving yields a 500 or leaves two defaults — a loser either cleared the winner
        // first (and won) or tripped the index (409, its own clear rolled back).
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            client.PostAsJsonAsync($"/api/projects/{projectId}/filters", new { name = $"Default {i}", isDefault = true })));

        Assert.All(responses, r =>
            Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"got {(int)r.StatusCode}"));
        Assert.Single(await ListAsync(client, projectId), v => v.IsDefault);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_views dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SavedFilterTests" -v q`
Expected: build FAILS — `SavedFilterView` has no `IsDefault`.

- [ ] **Step 3: Entity, records and index**

In `src/JiraUnpacked.Domain/Entities/SavedFilter.cs`, add after `DefinitionJson`:

```csharp
    /// <summary>
    /// The view that opens when this project's issue list is opened with nothing in its URL. At
    /// most one per project — a partial unique index enforces it (SavedFilterConfiguration).
    /// </summary>
    public bool IsDefault { get; set; }
```

In `ISavedFilterService.cs`, change `SavedFilterView` and `SaveFilterRequest` to:

```csharp
public sealed record SavedFilterView(
    Guid Id, string Name,
    List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns, string? SortBy, bool? SortDescending,
    bool IsDefault,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Body of the create and update calls. Columns/SortBy/SortDescending are the list's
/// presentation; left out, the view stores nothing for them and the list uses its defaults.
/// IsDefault left out: false on create, unchanged on update.
/// </summary>
public sealed record SaveFilterRequest(
    string? Name, List<FilterCondition>? Conditions, FilterGroup? Filter,
    List<string>? Columns = null, string? SortBy = null, bool? SortDescending = null,
    bool? IsDefault = null)
{
    public ReportFilter ToFilter() => new(Conditions, Filter);
}
```

(Keep the `SavedFilterView` doc comment from Task 1 above it.)

In `SavedFilterConfiguration.cs`, add inside the class, above `Configure`:

```csharp
    /// <summary>The partial unique index that allows one default view per project.</summary>
    public const string OneDefaultIndex = "IX_SavedFilters_OneDefaultPerProject";
```

and at the end of `Configure`:

```csharp
        // At most one default view per project, enforced by the database. Partial: the index
        // holds only rows with the flag set, so any number of non-defaults coexist.
        b.HasIndex(x => x.JiraProjectId)
            .IsUnique()
            .HasFilter("\"IsDefault\"")
            .HasDatabaseName(OneDefaultIndex);
```

Do **not** add `HasDefaultValue(false)` (EF warns that a bool default equal to the CLR default is never sent); the generated `AddColumn` carries `defaultValue: false`, which is what gives the column its database default.

- [ ] **Step 4: Generate the migration**

```bash
cd C:/jw-filters
dotnet tool restore
dotnet ef migrations add AddSavedFilterDefault -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api -o Persistence/Migrations
```

Open the generated `*_AddSavedFilterDefault.cs`. `Up` must contain exactly these two operations and nothing else (no FK or other index changes):

```csharp
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "SavedFilters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_SavedFilters_OneDefaultPerProject",
                table: "SavedFilters",
                column: "JiraProjectId",
                unique: true,
                filter: "\"IsDefault\"");
```

and `Down` drops the index, then the column. If anything else appears, stop and report it rather than editing the migration by hand.

Run: `dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api`
Expected: `No changes have been made to the model since the last migration.`

- [ ] **Step 5: Default handling and list order in the service**

In `src/JiraUnpacked.Infrastructure/Reporting/SavedFilterService.cs`, add `using JiraUnpacked.Infrastructure.Persistence.Configurations;` to the usings.

(a) In `ListAsync`, replace the ordering line with:

```csharp
            // The dropdown's order: the default first, then by name as a person reads it.
            .OrderByDescending(f => f.IsDefault).ThenBy(f => f.Name.ToLower()).ThenBy(f => f.Id)
```

(b) Replace everything in `CreateAsync` from `var now = ToStoragePrecision(_clock.UtcNow);` to the end of the method with:

```csharp
        var now = ToStoragePrecision(_clock.UtcNow);
        var row = new SavedFilter
        {
            Id = Guid.NewGuid(),
            JiraProjectId = projectId,
            Name = name!,
            DefinitionJson = definition!,
            IsDefault = request.IsDefault ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.SavedFilters.Add(row);   // EndUserId is stamped by AppDbContext.SaveChanges.
        return await SaveAsync(row, ct);
    }
```

(c) Replace everything in `UpdateAsync` from `row.Name = name!;` to the end of the method with:

```csharp
        row.Name = name!;
        row.DefinitionJson = definition!;
        // Left out = unchanged, so renaming a view never silently un-defaults it.
        if (request.IsDefault is { } isDefault) row.IsDefault = isDefault;
        row.UpdatedAt = ToStoragePrecision(_clock.UtcNow);

        return await SaveAsync(row, ct);
    }
```

(d) Delete the `IsDuplicateName` method (its doc comment moves into `SaveAsync` below) and add:

```csharp
    /// <summary>
    /// Writes the pending add/update. Taking the default clears the project's previous default
    /// FIRST, in the same transaction: the partial unique index is checked per statement, so
    /// writing the new default before clearing the old one would trip it.
    ///
    /// The unique indexes are the REAL guards; the pre-checks only give the common case a clean
    /// answer without provoking an exception. A concurrent writer that still wins a race trips an
    /// index, and that maps to the same 409 — a lost race never surfaces as a 500. Same shape as
    /// RegistrationService, JiraProjectService and SyncOrchestrator. A failed save rolls back the
    /// clear with it, so a loser never leaves the project with no default.
    /// </summary>
    private async Task<SaveFilterResult> SaveAsync(SavedFilter row, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        if (row.IsDefault)
        {
            await _db.SavedFilters
                .Where(f => f.JiraProjectId == row.JiraProjectId && f.IsDefault && f.Id != row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsDefault, false), ct);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pg)
        {
            return pg.ConstraintName == SavedFilterConfiguration.OneDefaultIndex
                ? SaveFilterResult.Conflict("Another view was made this project's default at the same time. Try again.")
                : DuplicateName(row.Name);
        }

        await tx.CommitAsync(ct);
        return SaveFilterResult.Ok(ToView(row));
    }
```

(e) In `CreateAsync` and `UpdateAsync`, the `NameTakenAsync` pre-checks stay as they are.

(f) In `ToView`, pass the flag:

```csharp
        return new SavedFilterView(
            row.Id, row.Name, d?.Conditions, d?.Filter, d?.Columns, d?.SortBy, d?.SortDescending,
            row.IsDefault, row.CreatedAt, row.UpdatedAt);
```

- [ ] **Step 6: Run the tests, the isolation gates and the lifecycle tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_views dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SavedFilterTests|FullyQualifiedName~TenantIsolationGateTests|FullyQualifiedName~TenantDeletionGateTests|FullyQualifiedName~Lifecycle" -v q`
Expected: PASS. The FR-B2 case (`TenantIsolationGateTests`) still inserts without `IsDefault` and stays green — the RLS policy is unchanged.

- [ ] **Step 7: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain src/JiraUnpacked.Infrastructure tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs
git commit -m "feat(views): one default view per project, list default-first" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `useListState` can open a whole view

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/useListState.ts`
- Test: `src/JiraUnpacked.Web/src/features/issues/useListState.test.tsx`

**Interfaces:**
- Produces: `export const LIST_PARAMS = ['filter', 'logic', 'sortBy', 'sortDesc', 'cols', 'view'] as const;`
- Produces: `export interface ListParams { filter: string | null; sortBy: string | null; sortDesc: string | null; cols: string | null; view: string | null }` — null = absent.
- Produces (new members of `useListState()`'s return): `viewId: string | null`, `hasListState: boolean`, `setViewId(id: string | null): void` (replace), `applyParams(next: ListParams, options: { replace: boolean }): void` (stable identity; removes every `LIST_PARAMS` key, then sets the non-null ones; holds the parsed tree at once).

- [ ] **Step 1: Write the failing tests**

In `src/JiraUnpacked.Web/src/features/issues/useListState.test.tsx`, change the import line to

```tsx
import { useListState, type ListParams } from './useListState';
```

add below `wrap`:

```tsx
/** Several history entries, starting on the last one — so a test can tell push from replace. */
const wrapAt = (entries: string[]) =>
  function Wrapper({ children }: { children: React.ReactNode }) {
    return <MemoryRouter initialEntries={entries} initialIndex={entries.length - 1}>{children}</MemoryRouter>;
  };
```

and append after the existing `describe`:

```tsx
const VIEW_FILTER = '{"logic":"Or","conditions":[{"field":"status","op":"IsNull"}]}';
const APPLIED: ListParams = {
  filter: VIEW_FILTER, sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary', view: 'v1',
};

describe('useListState — opening a saved view', () => {
  it('replaces the whole view in one history entry, and Back returns to what the list showed', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary&logic=Or') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false }));

    // Every list param is the view's; the stale legacy `logic` is gone with the rest.
    expect(Object.fromEntries(result.current.params)).toEqual({
      filter: VIEW_FILTER, sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary', view: 'v1',
    });
    expect(result.current.state.viewId).toBe('v1');
    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.sortBy).toBe('priority');
    expect(result.current.state.sortDescending).toBe(false);
    expect(result.current.state.columns).toEqual(['jiraKey', 'summary']);

    act(() => result.current.navigate(-1));

    expect(result.current.params.get('sortBy')).toBe('summary');
    expect(result.current.state.viewId).toBeNull();
    expect(result.current.state.tree.conditions).toEqual([]);
  });

  it('with replace, leaves no entry behind: Back skips the list it stood in for', () => {
    const { result } = renderHook(useProbe, {
      wrapper: wrapAt(['/elsewhere?from=before', '/projects/p1/issues']),
    });

    act(() => result.current.state.applyParams(APPLIED, { replace: true }));
    act(() => result.current.navigate(-1));

    expect(result.current.params.get('from')).toBe('before');
  });

  it('an applied view wins over an edit queued in the same tick', () => {
    // The URL already holds the view's filter, so it "does not move" — only a tree held at once
    // by applyParams (not one waited for from the URL) can undo the queued edit.
    const { result } = renderHook(useProbe, {
      wrapper: wrap(`/projects/p1/issues?filter=${encodeURIComponent(VIEW_FILTER)}`),
    });

    act(() => {
      result.current.state.setTree(ONE_ROW);
      result.current.state.applyParams(APPLIED, { replace: false });
    });

    expect(result.current.params.get('filter')).toBe(VIEW_FILTER);
    expect(result.current.state.tree.logic).toBe('Or');
    expect(result.current.state.tree.conditions).toEqual([
      expect.objectContaining({ field: 'status', op: 'IsNull' }),
    ]);
  });

  it('an edit right after applying builds on the applied URL, keeping view= and the view’s sort', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => {
      result.current.state.applyParams(APPLIED, { replace: false });
      result.current.state.setTree(ONE_ROW);
    });

    expect(result.current.params.get('view')).toBe('v1');
    expect(result.current.params.get('sortBy')).toBe('priority');
    expect(JSON.parse(result.current.params.get('filter')!)).toEqual({
      logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'l' }],
    });
    expect(result.current.state.tree.conditions[0].id).toBe('row-1');
  });

  it('Clear all keeps view=, so the list can show the view as modified', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues') });

    act(() => result.current.state.applyParams(APPLIED, { replace: false }));
    act(() => result.current.state.setTree(emptyRoot()));

    expect(result.current.params.has('filter')).toBe(false);
    expect(result.current.params.get('view')).toBe('v1');
  });

  it('setViewId adds or drops view= and nothing else', () => {
    const { result } = renderHook(useProbe, { wrapper: wrap('/projects/p1/issues?sortBy=summary&view=gone') });

    act(() => result.current.state.setViewId(null));
    expect(result.current.params.has('view')).toBe(false);
    expect(result.current.params.get('sortBy')).toBe('summary');

    act(() => result.current.state.setViewId('v2'));
    expect(result.current.state.viewId).toBe('v2');
  });

  it.each([
    ['/projects/p1/issues', false],
    ['/projects/p1/issues?unrelated=1', false],
    ['/projects/p1/issues?view=v1', true],
    ['/projects/p1/issues?cols=jiraKey', true],
    ['/projects/p1/issues?sortDesc=false', true],
    ['/projects/p1/issues?logic=Or', true],
  ])('knows whether %s carries any list state', (url, expected) => {
    const { result } = renderHook(useProbe, { wrapper: wrap(url) });
    expect(result.current.state.hasListState).toBe(expected);
  });
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/useListState.test.tsx`
Expected: the new tests FAIL (`applyParams is not a function`, `hasListState` undefined); typecheck of the test file fails on `ListParams`.

- [ ] **Step 3: Implement**

In `src/JiraUnpacked.Web/src/features/issues/useListState.ts`, add below `REMEMBERED_WRITES`:

```ts
/** Every URL param that is part of the list's view. A URL with none of them is a bare list. */
export const LIST_PARAMS = ['filter', 'logic', 'sortBy', 'sortDesc', 'cols', 'view'] as const;

/**
 * A whole list view as URL params, spelled exactly as this hook writes them: null = absent, which
 * for sortBy/sortDesc/cols means the default. `view` names the saved view the list was opened from.
 */
export interface ListParams {
  filter: string | null;
  sortBy: string | null;
  sortDesc: string | null;
  cols: string | null;
  view: string | null;
}
```

Inside `useListState`, directly after the `setTree` callback, add:

```ts
  /**
   * Replaces the whole view in one navigation — how a saved view is opened. Push keeps what the
   * list showed one Back away; replace leaves nothing behind (a default view standing in for a
   * bare URL).
   *
   * The applied filter is this hook's own write, exactly like setTree's: the tree is held at once
   * and the URL's arrival is recognised as its echo. Holding it here, rather than waiting to adopt
   * it from the URL, is what makes the view win over an edit queued in the same tick (the URL may
   * not even move), and keeps a render in which the URL has not caught up yet from showing the
   * old tree. The same no-op rule as setTree applies to what is remembered.
   */
  const applyParams = useCallback((next: ListParams, options: { replace: boolean }) => {
    const current = latestParams.current;
    const unchanged = next.filter === current.get('filter') && !current.has('logic');
    if (!unchanged) {
      recentWrites.current = [...recentWrites.current.slice(-(REMEMBERED_WRITES - 1)), next.filter ?? ''];
    }
    const out = new URLSearchParams(current);
    for (const key of LIST_PARAMS) out.delete(key);
    for (const [key, value] of Object.entries(next)) {
      if (value !== null) out.set(key, value);
    }
    const tree = parseFilterParam(next.filter, null);
    setHeld((h) => ({ ...h, tree }));
    latestParams.current = out;
    setParams(out, { replace: options.replace });
  }, [setParams]);

  const setViewId = useCallback((id: string | null) => patch({ view: id }), [patch]);
```

and add to the returned object (after `columns,`):

```ts
    /** The saved view the list was opened from, if any; kept through edits so "modified" can show. */
    viewId: params.get('view'),
    /** False for a bare URL — the only case in which a default view may step in. */
    hasListState: LIST_PARAMS.some((key) => params.has(key)),
    setViewId,
    applyParams,
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/useListState.test.tsx && npm run typecheck && npm run lint`
Expected: PASS (existing adoption/echo tests included).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues/useListState.ts src/JiraUnpacked.Web/src/features/issues/useListState.test.tsx
git commit -m "feat(web): useListState can open a whole saved view in one navigation" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: View model — `toSavable`, API module, `viewState`

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`
- Test: `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`
- Create: `src/JiraUnpacked.Web/src/api/views.ts`
- Create: `src/JiraUnpacked.Web/src/api/views.test.ts`
- Modify: `src/JiraUnpacked.Web/src/api/queryKeys.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/viewState.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`

**Interfaces:**
- Consumes: `ListParams`, `DEFAULT_COLUMNS`, `DEFAULT_SORT` (Task 3 / existing `useListState.ts`); `parseFilterParam`, `toUrlJson`, `emptyRoot`, `isCompleteCondition`, `toWireCondition` (`filterTree.ts`); `ISSUE_COLUMNS` (`columns.ts`).
- Produces: `toSavable(root: EditorGroup): FilterGroup | undefined` in `filterTree.ts`.
- Produces (`api/views.ts`): `interface SavedView { id: string; name: string; conditions: FilterCondition[] | null; filter: FilterGroup | null; columns: string[] | null; sortBy: string | null; sortDescending: boolean | null; isDefault: boolean; createdAt: string; updatedAt: string }`; `interface SaveViewRequest { name: string; filter?: FilterGroup; columns?: string[]; sortBy?: string; sortDescending?: boolean; isDefault?: boolean }`; `listViews(projectId): Promise<SavedView[]>`; `createView(projectId, body): Promise<SavedView>`; `updateView(projectId, viewId, body): Promise<SavedView>`; `deleteView(projectId, viewId): Promise<void>`.
- Produces: `queryKeys.views(projectId)` = `['projects', projectId, 'views']`.
- Produces (`viewState.ts`): `interface ListSnapshot { tree: EditorGroup; sortBy: string; sortDescending: boolean; columns: string[] }`; `shownColumns(columns: readonly string[]): string[]`; `viewTree(view: SavedView): EditorGroup`; `viewParams(view: SavedView): ListParams`; `isModified(view: SavedView, current: ListSnapshot): boolean`; `saveRequest(name: string, current: ListSnapshot): SaveViewRequest`; `resaveRequest(view: SavedView, changes: { name?: string; isDefault?: boolean }): SaveViewRequest`; `sortViews(views: SavedView[]): SavedView[]`; `withView(views: SavedView[], saved: SavedView): SavedView[]`; `withoutView(views: SavedView[], id: string): SavedView[]`.

- [ ] **Step 1: Write the failing `toSavable` tests**

In `filterTree.test.ts`, add `toSavable` to the import from `./filterTree`, and append:

```ts
describe('toSavable', () => {
  it('keeps disabled parts, drops unfinished rows and the groups they empty, and strips ids', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'status', op: 'Eq', value: 'Done' }),
        cond('b', { field: 'status', op: 'Eq', value: '' }),
        cond('c', { field: 'priority', op: 'Eq', value: 'High', disabled: true }),
      ],
      groups: [
        group('g1', { logic: 'Or', disabled: true, conditions: [cond('d', { field: 'assignee', op: 'Eq', value: 'Bob' })] }),
        group('g2', { conditions: [cond('e', { field: 'sprint', op: 'In', values: [] })] }),
      ],
    });

    expect(toSavable(tree)).toEqual({
      logic: 'And',
      conditions: [
        { field: 'status', op: 'Eq', value: 'Done' },
        { field: 'priority', op: 'Eq', value: 'High', disabled: true },
      ],
      groups: [{ logic: 'Or', disabled: true, conditions: [{ field: 'assignee', op: 'Eq', value: 'Bob' }] }],
    });
  });

  it('writes (Empty) on is / is not as is empty / is not empty — the only form the server accepts', () => {
    const tree = group('r', { conditions: [cond('a', { field: 'sprint', op: 'Neq', value: null, disabled: true })] });

    expect(toSavable(tree)).toEqual({
      logic: 'And', conditions: [{ field: 'sprint', op: 'IsNotNull', disabled: true }],
    });
  });

  it('saves no filter for an empty tree, or one holding only unfinished rows', () => {
    expect(toSavable(emptyRoot())).toBeUndefined();
    expect(toSavable(group('r', { conditions: [cond('a', { field: 'status', op: 'In', values: [] })] }))).toBeUndefined();
  });

  it('keeps a switched-off root with its contents', () => {
    const tree = group('r', { disabled: true, conditions: [cond('a', { field: 'summary', op: 'Contains', value: 'x' })] });

    expect(toSavable(tree)).toEqual({
      logic: 'And', disabled: true, conditions: [{ field: 'summary', op: 'Contains', value: 'x' }],
    });
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/filterTree.test.ts`
Expected: FAIL — `toSavable` is not exported.

- [ ] **Step 2: Implement `toSavable`**

In `filterTree.ts`, add directly after `pruneTree`:

```ts
/**
 * What a saved view stores: the tree as the user built it — disabled parts KEPT, so switching one
 * back on after opening the view works — minus what is not a filter yet: unfinished conditions,
 * and groups left empty by removing them. Ids stripped; (Empty) on is / is not in its wire form
 * (the server refuses a null `value`). Undefined when nothing is left: an empty tree saves no filter.
 *
 * Deterministic key order, so two trees that mean the same thing stringify the same — which is
 * how "modified" is decided (see viewState.isModified).
 */
export function toSavable(root: EditorGroup): FilterGroup | undefined {
  const conditions = root.conditions
    .filter(isCompleteCondition)
    .map((c) => (c.disabled ? { ...toWireCondition(c), disabled: true } : toWireCondition(c)));
  const groups = root.groups
    .map((g) => toSavable(g))
    .filter((g): g is FilterGroup => g !== undefined);
  if (conditions.length === 0 && groups.length === 0) return undefined;
  return {
    logic: root.logic,
    ...(conditions.length > 0 ? { conditions } : {}),
    ...(groups.length > 0 ? { groups } : {}),
    ...(root.disabled ? { disabled: true } : {}),
  };
}
```

Run: `npx vitest run src/features/issues/filterTree.test.ts` → PASS.

- [ ] **Step 3: Write the failing API module test**

Create `src/JiraUnpacked.Web/src/api/views.test.ts`:

```ts
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../test/setup';
import { createView, deleteView, listViews, updateView } from './views';

describe('saved views API', () => {
  it('lists, creates, updates and deletes through the project’s /filters route', async () => {
    const seen: string[] = [];
    const bodies: unknown[] = [];
    server.use(
      http.get('/api/projects/p1/filters', () => {
        seen.push('GET');
        return HttpResponse.json([]);
      }),
      http.post('/api/projects/p1/filters', async ({ request }) => {
        seen.push('POST');
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'v1' }, { status: 201 });
      }),
      http.put('/api/projects/p1/filters/v1', async ({ request }) => {
        seen.push('PUT');
        bodies.push(await request.json());
        return HttpResponse.json({ id: 'v1' });
      }),
      http.delete('/api/projects/p1/filters/v1', () => {
        seen.push('DELETE');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    await listViews('p1');
    await createView('p1', { name: 'A', sortBy: 'summary' });
    await updateView('p1', 'v1', { name: 'B', isDefault: true });
    await deleteView('p1', 'v1');

    expect(seen).toEqual(['GET', 'POST', 'PUT', 'DELETE']);
    expect(bodies).toEqual([{ name: 'A', sortBy: 'summary' }, { name: 'B', isDefault: true }]);
  });

  it('surfaces a refusal’s title as an ApiError', async () => {
    server.use(http.post('/api/projects/p1/filters', () =>
      HttpResponse.json({ title: "A saved filter named 'A' already exists in this project." }, { status: 409 })));

    await expect(createView('p1', { name: 'A' })).rejects.toMatchObject({
      status: 409, title: "A saved filter named 'A' already exists in this project.",
    });
  });
});
```

Run: `npx vitest run src/api/views.test.ts` → FAIL (module not found).

- [ ] **Step 4: Implement the API module and query key**

Create `src/JiraUnpacked.Web/src/api/views.ts`:

```ts
import { apiFetch } from './client';
import type { FilterCondition, FilterGroup } from './issues';

/**
 * A saved view: a filter plus the list's columns and sort, private to its user and project. The
 * API still calls it a saved filter (the route is /filters); the SPA calls it what it now is.
 * Every member the view does not store comes back null — "use the list's default".
 * `conditions` is the flat list older clients could write; this SPA only ever writes `filter`.
 */
export interface SavedView {
  id: string;
  name: string;
  conditions: FilterCondition[] | null;
  filter: FilterGroup | null;
  columns: string[] | null;
  sortBy: string | null;
  sortDescending: boolean | null;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * Body of create and update. Leave a member out to store nothing for it. `isDefault` left out:
 * false on create, unchanged on update; true makes this the project's only default.
 */
export interface SaveViewRequest {
  name: string;
  filter?: FilterGroup;
  columns?: string[];
  sortBy?: string;
  sortDescending?: boolean;
  isDefault?: boolean;
}

const base = (projectId: string) => `/api/projects/${projectId}/filters`;

/** Default first, then by name (the server's order). */
export const listViews = (projectId: string) => apiFetch<SavedView[]>(base(projectId));

export const createView = (projectId: string, body: SaveViewRequest) =>
  apiFetch<SavedView>(base(projectId), { method: 'POST', body });

export const updateView = (projectId: string, viewId: string, body: SaveViewRequest) =>
  apiFetch<SavedView>(`${base(projectId)}/${viewId}`, { method: 'PUT', body });

export const deleteView = (projectId: string, viewId: string) =>
  apiFetch<void>(`${base(projectId)}/${viewId}`, { method: 'DELETE' });
```

In `src/JiraUnpacked.Web/src/api/queryKeys.ts`, add after `queries`:

```ts
  views: (projectId: string) => ['projects', projectId, 'views'] as const,
```

Run: `npx vitest run src/api/views.test.ts` → PASS.

- [ ] **Step 5: Write the failing `viewState` tests**

Create `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import type { SavedView } from '../../api/views';
import { parseFilterParam, type EditorCondition } from './filterTree';
import { DEFAULT_COLUMNS } from './useListState';
import {
  isModified, resaveRequest, saveRequest, shownColumns, sortViews, viewParams, withView, withoutView,
  type ListSnapshot,
} from './viewState';

const view = (overrides: Partial<SavedView> = {}): SavedView => ({
  id: 'v1', name: 'My backlog', conditions: null, filter: null, columns: null, sortBy: null,
  sortDescending: null, isDefault: false, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-01T00:00:00Z',
  ...overrides,
});

/** A group exactly as the API serialises it: every absent member an explicit null. */
const SERVER_FILTER = {
  logic: 'And',
  conditions: [
    { field: 'summary', op: 'Contains', value: 'login', values: null, disabled: null },
    { field: 'sprint', op: 'IsNull', value: null, values: null, disabled: true },
  ],
  groups: null,
  disabled: null,
} as unknown as SavedView['filter'];

const BACKLOG = view({
  filter: SERVER_FILTER, columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
});

/** The list right after opening `v`: what useListState reads back from viewParams(v). */
const opened = (v: SavedView): ListSnapshot => {
  const p = viewParams(v);
  return {
    tree: parseFilterParam(p.filter, null),
    sortBy: p.sortBy ?? 'updatedAt',
    sortDescending: p.sortDesc !== 'false',
    columns: p.cols?.split(',') ?? DEFAULT_COLUMNS,
  };
};

const withConditions = (s: ListSnapshot, conditions: EditorCondition[]): ListSnapshot =>
  ({ ...s, tree: { ...s.tree, conditions } });

describe('viewParams', () => {
  it('opens a view the server returned with explicit nulls as the clean URL useListState writes', () => {
    const p = viewParams(BACKLOG);

    expect(JSON.parse(p.filter!)).toEqual({
      logic: 'And',
      conditions: [
        { field: 'summary', op: 'Contains', value: 'login' },
        { field: 'sprint', op: 'IsNull', disabled: true },
      ],
    });
    expect(p).toMatchObject({ sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary,priority', view: 'v1' });
  });

  it('writes what a view leaves out — or spells out as the default — as absent', () => {
    const blank = { filter: null, sortBy: null, sortDesc: null, cols: null, view: 'v1' };
    expect(viewParams(view())).toEqual(blank);
    expect(viewParams(view({ sortBy: 'updatedAt', sortDescending: true, columns: [...DEFAULT_COLUMNS] }))).toEqual(blank);
    // An empty column list would show a table with no columns: it is treated as "not stored".
    expect(viewParams(view({ columns: [] }))).toEqual(blank);
  });

  it('opens a flat conditions list, as older clients stored it, as an All group', () => {
    const p = viewParams(view({ conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] }));

    expect(JSON.parse(p.filter!)).toEqual({ logic: 'And', conditions: [{ field: 'status', op: 'Eq', value: 'Done' }] });
  });

  it('writes columns in table order, whatever order they were stored in', () => {
    expect(viewParams(view({ columns: ['priority', 'jiraKey'] })).cols).toBe('jiraKey,priority');
  });
});

describe('isModified', () => {
  it('is false right after opening a view, true after an edit, false again once it is undone', () => {
    const now = opened(BACKLOG);
    const [summary, sprint] = now.tree.conditions;

    expect(isModified(BACKLOG, now)).toBe(false);
    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, value: 'logout' }, sprint]))).toBe(true);
    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, value: 'login' }, sprint]))).toBe(false);
  });

  it('counts sort and columns, but not the order the columns were listed in', () => {
    const now = opened(BACKLOG);

    expect(isModified(BACKLOG, { ...now, sortDescending: true })).toBe(true);
    expect(isModified(BACKLOG, { ...now, sortBy: 'summary' })).toBe(true);
    expect(isModified(BACKLOG, { ...now, columns: ['jiraKey', 'summary'] })).toBe(true);
    expect(isModified(BACKLOG, { ...now, columns: ['priority', 'summary', 'jiraKey'] })).toBe(false);
  });

  it('ignores a row that is still being filled in — Save would not write it', () => {
    const now = opened(BACKLOG);
    const blank: EditorCondition = { id: 'x', field: 'status', op: 'Eq', value: '' };

    expect(isModified(BACKLOG, withConditions(now, [...now.tree.conditions, blank]))).toBe(false);
  });

  it('counts switching a row off, since a view stores disabled parts', () => {
    const now = opened(BACKLOG);
    const [summary, sprint] = now.tree.conditions;

    expect(isModified(BACKLOG, withConditions(now, [{ ...summary, disabled: true }, sprint]))).toBe(true);
  });

  it('treats a view that stores nothing as the default list', () => {
    const plain: ListSnapshot = {
      tree: parseFilterParam(null, null), sortBy: 'updatedAt', sortDescending: true, columns: DEFAULT_COLUMNS,
    };

    expect(isModified(view(), plain)).toBe(false);
    expect(isModified(view(), { ...plain, sortBy: 'summary' })).toBe(true);
  });
});

describe('saveRequest', () => {
  it('stores the list as shown: savable filter, columns in table order, sort always', () => {
    const now = opened(BACKLOG);

    expect(saveRequest('Mine', { ...now, columns: ['priority', 'jiraKey', 'nope'] })).toEqual({
      name: 'Mine',
      filter: {
        logic: 'And',
        conditions: [
          { field: 'summary', op: 'Contains', value: 'login' },
          { field: 'sprint', op: 'IsNull', disabled: true },
        ],
      },
      columns: ['jiraKey', 'priority'],
      sortBy: 'priority',
      sortDescending: false,
    });
  });

  it('sends no filter for an empty tree', () => {
    expect(saveRequest('All', opened(view()))).toEqual({
      name: 'All', columns: DEFAULT_COLUMNS, sortBy: 'updatedAt', sortDescending: true,
    });
  });
});

describe('resaveRequest', () => {
  it('keeps exactly what the view stores, cleaned of the server’s nulls, under a new name', () => {
    expect(resaveRequest(BACKLOG, { name: 'Login bugs' })).toEqual({
      name: 'Login bugs',
      filter: {
        logic: 'And',
        conditions: [
          { field: 'summary', op: 'Contains', value: 'login' },
          { field: 'sprint', op: 'IsNull', disabled: true },
        ],
      },
      columns: ['jiraKey', 'summary', 'priority'],
      sortBy: 'priority',
      sortDescending: false,
    });
  });

  it('moves only the default flag, storing nothing the view did not store', () => {
    expect(resaveRequest(view(), { isDefault: true })).toEqual({ name: 'My backlog', isDefault: true });
  });
});

describe('the cached list', () => {
  const a = view({ id: 'a', name: 'apple', isDefault: true });
  const b = view({ id: 'b', name: 'Banana' });

  it('orders default first, then by name ignoring case', () => {
    expect(sortViews([b, view({ id: 'c', name: 'cherry' }), a]).map((v) => v.id)).toEqual(['a', 'b', 'c']);
  });

  it('replaces a saved view in place and leaves it the only default', () => {
    const next = withView([a, b], { ...b, name: 'Blueberry', isDefault: true });

    expect(next.map((v) => [v.name, v.isDefault])).toEqual([['Blueberry', true], ['apple', false]]);
  });

  it('adds a new view and drops a deleted one', () => {
    expect(withView([a], b).map((v) => v.id)).toEqual(['a', 'b']);
    expect(withoutView([a, b], 'a').map((v) => v.id)).toEqual(['b']);
  });
});

describe('shownColumns', () => {
  it('is table order, known ids only, each once', () => {
    expect(shownColumns(['priority', 'jiraKey', 'nope', 'jiraKey'])).toEqual(['jiraKey', 'priority']);
  });
});
```

Run: `npx vitest run src/features/issues/viewState.test.ts` → FAIL (module not found).

- [ ] **Step 6: Implement `viewState.ts`**

Create `src/JiraUnpacked.Web/src/features/issues/viewState.ts`:

```ts
import type { SavedView, SaveViewRequest } from '../../api/views';
import { ISSUE_COLUMNS } from './columns';
import { emptyRoot, parseFilterParam, toSavable, toUrlJson, type EditorGroup } from './filterTree';
import { DEFAULT_COLUMNS, DEFAULT_SORT, type ListParams } from './useListState';

/** What the list shows right now — the part of it a view can store. */
export interface ListSnapshot {
  tree: EditorGroup;
  sortBy: string;
  sortDescending: boolean;
  columns: string[];
}

const COLUMN_ORDER = ISSUE_COLUMNS.map((c) => c.id);

/** Columns as the table shows them: table order, ids the table knows, each once. */
export const shownColumns = (columns: readonly string[]): string[] =>
  COLUMN_ORDER.filter((id) => columns.includes(id));

// The URL's own normalisation (see useListState): a default is an absent param.
const colsParam = (columns: readonly string[]): string | null => {
  const joined = shownColumns(columns).join(',');
  return joined === DEFAULT_COLUMNS.join(',') ? null : joined;
};
const sortByParam = (sortBy: string): string | null => (sortBy === DEFAULT_SORT ? null : sortBy);
const sortDescParam = (descending: boolean): string | null => (descending ? null : 'false');

// What a view leaves out is the list's default. An empty column list would show a table with no
// columns at all, so it counts as "not stored" too.
const viewSortBy = (v: SavedView): string => v.sortBy ?? DEFAULT_SORT;
const viewSortDescending = (v: SavedView): boolean => v.sortDescending ?? true;
const viewColumns = (v: SavedView): readonly string[] =>
  v.columns && v.columns.length > 0 ? v.columns : DEFAULT_COLUMNS;

/**
 * A view's filter as the editor holds it. Read through the same parser as a link, so the API's
 * explicit nulls (`"value": null`, `"groups": null`) and a flat `conditions` list written by an
 * older client come out as the one tree shape everything else here compares.
 */
export function viewTree(view: SavedView): EditorGroup {
  if (view.filter) return parseFilterParam(JSON.stringify(view.filter), null);
  if (view.conditions && view.conditions.length > 0) {
    return parseFilterParam(JSON.stringify(view.conditions), 'And');
  }
  return emptyRoot();
}

/** The URL a view opens at, spelled exactly as useListState writes it, plus view=<id>. */
export function viewParams(view: SavedView): ListParams {
  return {
    filter: toUrlJson(viewTree(view)),
    sortBy: sortByParam(viewSortBy(view)),
    sortDesc: sortDescParam(viewSortDescending(view)),
    cols: colsParam(viewColumns(view)),
    view: view.id,
  };
}

function fingerprint(tree: EditorGroup, sortBy: string, descending: boolean, columns: readonly string[]): string {
  return JSON.stringify([toSavable(tree) ?? null, sortByParam(sortBy), sortDescParam(descending), colsParam(columns)]);
}

/**
 * Whether the list differs from the view in anything Save would write. Both sides go through
 * toSavable and the URL's normalisation (defaults ≡ absent, columns in table order), so a row that
 * is still being filled in, or columns listed in another order, is not a change — and saving always
 * clears the marker.
 */
export function isModified(view: SavedView, current: ListSnapshot): boolean {
  return fingerprint(viewTree(view), viewSortBy(view), viewSortDescending(view), viewColumns(view))
    !== fingerprint(current.tree, current.sortBy, current.sortDescending, current.columns);
}

/** The body that stores the list as it is now under `name` (isDefault left out: see SaveViewRequest). */
export function saveRequest(name: string, current: ListSnapshot): SaveViewRequest {
  const filter = toSavable(current.tree);
  return {
    name,
    ...(filter ? { filter } : {}),
    columns: shownColumns(current.columns),
    sortBy: current.sortBy,
    sortDescending: current.sortDescending,
  };
}

/**
 * The body that renames a view or moves the default flag while keeping exactly what it stores.
 * A PUT replaces the whole definition, so the stored one is sent back — never the list's edits.
 */
export function resaveRequest(view: SavedView, changes: { name?: string; isDefault?: boolean }): SaveViewRequest {
  const filter = toSavable(viewTree(view));
  return {
    name: changes.name ?? view.name,
    ...(filter ? { filter } : {}),
    ...(view.columns ? { columns: view.columns } : {}),
    ...(view.sortBy !== null ? { sortBy: view.sortBy } : {}),
    ...(view.sortDescending !== null ? { sortDescending: view.sortDescending } : {}),
    ...(changes.isDefault !== undefined ? { isDefault: changes.isDefault } : {}),
  };
}

/** The dropdown's order, the server's: the default first, then by name ignoring case. */
export function sortViews(views: SavedView[]): SavedView[] {
  return [...views].sort((a, b) =>
    Number(b.isDefault) - Number(a.isDefault) || a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
}

/** The cached list after `saved` was written: replaced or added, and the only default if it is one. */
export function withView(views: SavedView[], saved: SavedView): SavedView[] {
  const others = views
    .filter((v) => v.id !== saved.id)
    .map((v) => (saved.isDefault && v.isDefault ? { ...v, isDefault: false } : v));
  return sortViews([...others, saved]);
}

export const withoutView = (views: SavedView[], id: string): SavedView[] => views.filter((v) => v.id !== id);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/viewState.test.ts src/features/issues/filterTree.test.ts src/api/views.test.ts && npm run typecheck && npm run lint`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues/filterTree.ts src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts src/JiraUnpacked.Web/src/api/views.ts src/JiraUnpacked.Web/src/api/views.test.ts src/JiraUnpacked.Web/src/api/queryKeys.ts src/JiraUnpacked.Web/src/features/issues/viewState.ts src/JiraUnpacked.Web/src/features/issues/viewState.test.ts
git commit -m "feat(web): saved-view model — toSavable, views API, modified check" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Views dropdown — open a view, modified marker, Save

**Files:**
- Create: `src/JiraUnpacked.Web/src/test/viewsFixture.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/useSavedViews.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/ViewsMenu.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`, `ColumnChooser.test.tsx`, `FilterBuilder.test.tsx` (register `noSavedViews`)
- Create: `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx`

**Interfaces:**
- Consumes: Task 3 (`viewId`, `hasListState`, `applyParams`, `setViewId`, `ListParams`), Task 4 (`SavedView`, `listViews`, `updateView`, `queryKeys.views`, `ListSnapshot`, `viewParams`, `isModified`, `saveRequest`, `withView`).
- Produces (`useSavedViews.ts`): `interface ViewListControls { viewId: string | null; hasListState: boolean; applyParams(next: ListParams, options: { replace: boolean }): void; setViewId(id: string | null): void }`; `interface SavedViews { views: SavedView[] | undefined; unavailable: boolean; loaded: SavedView | undefined; modified: boolean; apply(view: SavedView): void }`; `useSavedViews(projectId: string, list: ViewListControls, snapshot: ListSnapshot): SavedViews`.
- Produces (`ViewsMenu.tsx`): `viewErrorMessage(error: unknown): string`; `ViewsMenu(props: { projectId; views; unavailable; loaded; modified; snapshot; onApply(view) })`.
- Produces (`test/viewsFixture.ts`): `noSavedViews` (MSW handler, any project → `[]`); `savedView(overrides: Partial<SavedView> & Pick<SavedView, 'id' | 'name'>): SavedView`; `mockViewsApi(initial: SavedView[], projectId = 'p1'): ViewsApiMock` with `views: SavedView[]` (live) and `requests: { method: 'POST' | 'PUT' | 'DELETE'; id?: string; body?: SaveViewRequest }[]`.

- [ ] **Step 1: Test fixture**

Create `src/JiraUnpacked.Web/src/test/viewsFixture.ts`:

```ts
import { http, HttpResponse } from 'msw';
import type { SavedView, SaveViewRequest } from '../api/views';
import { server } from './setup';

/** No saved views, for any project: for tests that mount IssueList but are not about views. */
export const noSavedViews = http.get('/api/projects/:projectId/filters', () => HttpResponse.json([]));

export function savedView(overrides: Partial<SavedView> & Pick<SavedView, 'id' | 'name'>): SavedView {
  return {
    conditions: null, filter: null, columns: null, sortBy: null, sortDescending: null, isDefault: false,
    createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-01T00:00:00Z',
    ...overrides,
  };
}

export interface ViewsApiMock {
  /** The server's current list — reassigned by every write, so read it through this object. */
  views: SavedView[];
  requests: { method: 'POST' | 'PUT' | 'DELETE'; id?: string; body?: SaveViewRequest }[];
}

/**
 * The saved-view endpoints of one project, backed by an in-memory list that behaves like the
 * server: names unique (409), one default (taking it clears the others), listed default-first then
 * by name. Stateful, because the SPA refetches after every write and a static list would undo it.
 */
export function mockViewsApi(initial: SavedView[], projectId = 'p1'): ViewsApiMock {
  const mock: ViewsApiMock = { views: [...initial], requests: [] };
  const base = `/api/projects/${projectId}/filters`;
  let created = 0;

  const sorted = () => [...mock.views].sort((a, b) =>
    Number(b.isDefault) - Number(a.isDefault) || a.name.toLowerCase().localeCompare(b.name.toLowerCase()));
  const nameTaken = (name: string, except?: string) => mock.views.some((v) => v.name === name && v.id !== except);
  const taken = (name: string) =>
    HttpResponse.json({ title: `A saved filter named '${name}' already exists in this project.` }, { status: 409 });
  const clearDefaults = () => {
    mock.views = mock.views.map((v) => (v.isDefault ? { ...v, isDefault: false } : v));
  };
  const fromBody = (id: string, body: SaveViewRequest, previous?: SavedView): SavedView => ({
    id,
    name: body.name,
    conditions: null,
    filter: body.filter ?? null,
    columns: body.columns ?? null,
    sortBy: body.sortBy ?? null,
    sortDescending: body.sortDescending ?? null,
    isDefault: body.isDefault ?? previous?.isDefault ?? false,
    createdAt: previous?.createdAt ?? '2026-09-23T00:00:00Z',
    updatedAt: '2026-09-23T12:00:00Z',
  });

  server.use(
    http.get(base, () => HttpResponse.json(sorted())),
    http.post(base, async ({ request }) => {
      const body = (await request.json()) as SaveViewRequest;
      mock.requests.push({ method: 'POST', body });
      if (nameTaken(body.name)) return taken(body.name);
      created += 1;
      const saved = fromBody(`created-${created}`, body);
      if (saved.isDefault) clearDefaults();
      mock.views = [...mock.views, saved];
      return HttpResponse.json(saved, { status: 201 });
    }),
    http.put(`${base}/:viewId`, async ({ request, params }) => {
      const id = String(params.viewId);
      const body = (await request.json()) as SaveViewRequest;
      mock.requests.push({ method: 'PUT', id, body });
      const previous = mock.views.find((v) => v.id === id);
      if (!previous) return new HttpResponse(null, { status: 404 });
      if (nameTaken(body.name, id)) return taken(body.name);
      const saved = fromBody(id, body, previous);
      if (saved.isDefault) clearDefaults();
      mock.views = mock.views.map((v) => (v.id === id ? saved : v));
      return HttpResponse.json(saved);
    }),
    http.delete(`${base}/:viewId`, ({ params }) => {
      const id = String(params.viewId);
      mock.requests.push({ method: 'DELETE', id });
      if (!mock.views.some((v) => v.id === id)) return new HttpResponse(null, { status: 404 });
      mock.views = mock.views.filter((v) => v.id !== id);
      return new HttpResponse(null, { status: 204 });
    }),
  );
  return mock;
}
```

- [ ] **Step 2: Existing IssueList-mounting tests register "no views"**

IssueList will fetch the project's views, and MSW errors on an unhandled request.

- `IssueList.test.tsx`: add `import { noSavedViews } from '../../test/viewsFixture';` and add `noSavedViews,` inside the `server.use(` in `beforeEach`, right after the `filter-catalog` handler.
- `ColumnChooser.test.tsx`: same import; add `noSavedViews,` inside `mockList`'s `server.use(` after the `filter-catalog` handler.
- `FilterBuilder.test.tsx`: same import; add `noSavedViews,` inside `mockFilterCatalog`'s `server.use(` after the `filter-catalog` handler.

- [ ] **Step 3: Write the failing end-to-end tests**

Create `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx`:

```tsx
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../test/setup';
import { FILTER_CATALOG } from '../../test/filterCatalogFixture';
import { mockViewsApi, savedView } from '../../test/viewsFixture';
import IssueList from './IssueList';

type User = ReturnType<typeof userEvent.setup>;

const ITEM = {
  jiraIssueId: '10001', jiraKey: 'ACME-1', summary: 'Login fails',
  status: 'Backlog', issueType: 'Bug', priority: 'High', assignee: 'Ada',
  sprint: 'PRP 20', team: 'CI/CD', storyPoints: 3,
  parentJiraIssueId: null, jiraCreatedAt: '2026-01-01T00:00:00Z', jiraUpdatedAt: '2026-02-01T00:00:00Z',
};

/** The router's current query string, where a test can read it. */
function LocationProbe() {
  const { search } = useLocation();
  return <output data-testid="location">{search}</output>;
}

function NavButtons() {
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate(-1)}>Go back</button>
      <button type="button" onClick={() => navigate('/projects/p1/issues')}>Open the bare list</button>
      <button type="button" onClick={() => navigate('/projects/p1/issues?sortBy=summary')}>
        Open the list sorted by summary
      </button>
    </>
  );
}

/** Renders IssueList on the last of `entries` (so earlier entries are what Back returns to). */
function renderList(...entries: string[]) {
  const initial = entries.length > 0 ? entries : ['/projects/p1/issues'];
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={initial} initialIndex={initial.length - 1}>
        <LocationProbe />
        <NavButtons />
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
          <Route path="*" element={<p>Somewhere else</p>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const urlParams = () => new URLSearchParams(screen.getByTestId('location').textContent ?? '');
const menu = () => screen.getByRole('group', { name: 'Saved views' });

/** Catalog, values and issues; returns every issue-query body in the order it was sent. */
function mockIssues(): Record<string, unknown>[] {
  const bodies: Record<string, unknown>[] = [];
  server.use(
    http.get('/api/filter-catalog', () => HttpResponse.json(FILTER_CATALOG)),
    http.get('/api/projects/p1/filter-values', () => new HttpResponse(null, { status: 500 })),
    http.post('/api/projects/p1/issues/query', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      bodies.push(body);
      return HttpResponse.json({ items: [ITEM], page: body.page ?? 1, pageSize: 50, totalCount: 120 });
    }),
  );
  return bodies;
}

const LOGIN_FILTER = { logic: 'And' as const, conditions: [{ field: 'summary', op: 'Contains' as const, value: 'login' }] };

const BACKLOG = savedView({
  id: 'v-backlog', name: 'My backlog', filter: LOGIN_FILTER,
  columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
});
const TRIAGE = savedView({ id: 'v-triage', name: 'Triage', sortBy: 'createdAt' });

/** Opens the menu and chooses a view (waiting for the list, which may still be loading). */
async function openView(user: User, name: string) {
  await user.click(screen.getByRole('button', { name: /^Views/ }));
  await user.click(await within(menu()).findByRole('button', { name }));
}

describe('saved views — opening, modified, Save', () => {
  it('opens a view: the URL, the request and the editor follow, and Back returns to what you had', async () => {
    const bodies = mockIssues();
    mockViewsApi([BACKLOG, TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await openView(user, 'My backlog');

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(Object.fromEntries(urlParams())).toEqual({
      filter: JSON.stringify(LOGIN_FILTER),
      sortBy: 'priority', sortDesc: 'false', cols: 'jiraKey,summary,priority', view: 'v-backlog',
    });
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('login');
    expect(screen.getByRole('button', { name: 'Sort by Priority' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sort by Status' })).not.toBeInTheDocument();
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({
      filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false, page: 1,
    }));

    await user.click(screen.getByRole('button', { name: 'Go back' }));

    expect(urlParams().get('sortBy')).toBe('summary');
    expect(urlParams().has('view')).toBe(false);
    expect(screen.getByRole('button', { name: 'Views' })).toBeInTheDocument();
  });

  it('starts an opened view on page 1, even when only its sort differs', async () => {
    const bodies = mockIssues();
    mockViewsApi([TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: /next/i }));
    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ page: 2 }));

    await openView(user, 'Triage');

    await waitFor(() => expect(bodies.at(-1)).toMatchObject({ page: 1, sortBy: 'createdAt' }));
  });

  it('marks the view modified on an edit, and unmarks it when the edit is undone', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    const value = screen.getByLabelText('Value for condition 1');
    await user.type(value, 's');
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(urlParams().get('view')).toBe('v-backlog');

    await user.type(value, '{Backspace}');
    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();

    // Sort is part of the view too.
    await user.click(screen.getByRole('button', { name: 'Sort by Summary' }));
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
  });

  it('Save overwrites the loaded view with the list as it is now, and the marker clears', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');

    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    // Name and default flag are kept: isDefault is left out, which the server reads as unchanged.
    expect(api.requests).toEqual([{
      method: 'PUT', id: 'v-backlog', body: {
        name: 'My backlog',
        filter: { logic: 'And', conditions: [{ field: 'summary', op: 'Contains', value: 'logins' }] },
        columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
      },
    }]);
  });

  it('shows the server’s reason when Save fails, and keeps the view marked modified', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    server.use(http.put('/api/projects/p1/filters/:viewId', () =>
      HttpResponse.json({ title: 'A saved filter definition may be at most 8192 bytes.' }, { status: 400 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');

    await user.click(await screen.findByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('A saved filter definition may be at most 8192 bytes.');
    expect(screen.getByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
  });

  it('closes the menu on Escape (focus back on the button) and on a click outside', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList();
    await screen.findByText('ACME-1');

    const trigger = screen.getByRole('button', { name: 'Views' });
    await user.click(trigger);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(await within(menu()).findByRole('button', { name: 'My backlog' })).toBeInTheDocument();

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();

    await user.click(trigger);
    await user.click(screen.getByText(/Page 1 of/));
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
  });

  it('says Views unavailable when the views cannot load, and the issue list still works', async () => {
    mockIssues();
    server.use(http.get('/api/projects/p1/filters', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    renderList();

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByText('Views unavailable')).toBeInTheDocument();
    expect(within(menu()).queryByRole('button')).not.toBeInTheDocument();
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/SavedViews.test.tsx`
Expected: FAIL — no `Views` button.

- [ ] **Step 4: `useSavedViews`**

Create `src/JiraUnpacked.Web/src/features/issues/useSavedViews.ts`:

```ts
import { useCallback, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { listViews, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import type { ListParams } from './useListState';
import { isModified, viewParams, type ListSnapshot } from './viewState';

/** The parts of useListState that saved views drive. */
export interface ViewListControls {
  viewId: string | null;
  hasListState: boolean;
  applyParams: (next: ListParams, options: { replace: boolean }) => void;
  setViewId: (id: string | null) => void;
}

export interface SavedViews {
  /** Undefined until loaded (or when the request failed). */
  views: SavedView[] | undefined;
  /** The request failed: the menu says so and the list carries on without views. */
  unavailable: boolean;
  /** The view named by `view=`, when it is in the list. */
  loaded: SavedView | undefined;
  /** The list differs from the loaded view in something Save would write. */
  modified: boolean;
  /** Opens a view: its whole URL, pushed, so Back returns to what the list showed. */
  apply: (view: SavedView) => void;
}

/** The project's saved views, and which of them — if any — the list is showing. */
export function useSavedViews(projectId: string, list: ViewListControls, snapshot: ListSnapshot): SavedViews {
  const { viewId, applyParams } = list;
  const query = useQuery({ queryKey: queryKeys.views(projectId), queryFn: () => listViews(projectId) });
  const views = query.data;

  const loaded = viewId ? views?.find((v) => v.id === viewId) : undefined;
  const modified = useMemo(() => (loaded ? isModified(loaded, snapshot) : false), [loaded, snapshot]);

  const apply = useCallback(
    (view: SavedView) => applyParams(viewParams(view), { replace: false }),
    [applyParams],
  );

  return { views, unavailable: query.isError, loaded, modified, apply };
}
```

- [ ] **Step 5: `ViewsMenu` (open, modified, Save)**

Create `src/JiraUnpacked.Web/src/features/issues/ViewsMenu.tsx`:

```tsx
import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { updateView, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import { Button } from '../../ui';
import { saveRequest, withView, type ListSnapshot } from './viewState';

const TRIGGER_CLASS =
  'rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]';
const ITEM_CLASS =
  'w-full rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] ' +
  'disabled:cursor-not-allowed disabled:opacity-60';

/** The server's own words for a refusal, except the clash the user fixes by typing: a taken name. */
export function viewErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.status === 409 && /already exists/i.test(error.title)
      ? 'A view with this name already exists'
      : error.title;
  }
  return 'Could not save the view.';
}

export interface ViewsMenuProps {
  projectId: string;
  views: SavedView[] | undefined;
  unavailable: boolean;
  loaded: SavedView | undefined;
  modified: boolean;
  /** The list as it is now — what Save writes. */
  snapshot: ListSnapshot;
  onApply: (view: SavedView) => void;
}

/**
 * The toolbar's saved views: a button naming the loaded view (and whether the list has moved away
 * from it), Save to overwrite it, and a popover listing the project's views.
 *
 * A controlled popover rather than a native <details> (ColumnChooser's choice): choosing a view must
 * close it, and later a failed save must keep it open — both are state this component owns.
 */
export function ViewsMenu({ projectId, views, unavailable, loaded, modified, snapshot, onApply }: ViewsMenuProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  // A click anywhere outside closes it, like every other popover.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || !open) return;
    event.preventDefault();
    setOpen(false);
    triggerRef.current?.focus();
  };

  /** Puts a saved view into the cached list at once (so the label follows), then asks the server. */
  const store = (saved: SavedView) => {
    queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withView(old ?? [], saved));
    void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
  };

  // Overwrites with the list as it is now; isDefault left out, so the flag is unchanged.
  const overwrite = useMutation({
    mutationFn: (view: SavedView) => updateView(projectId, view.id, saveRequest(view.name, snapshot)),
    onSuccess: store,
  });

  const label = loaded ? `Views: ${loaded.name}${modified ? ' • modified' : ''}` : 'Views';

  return (
    <div ref={rootRef} className="relative" onKeyDown={onKeyDown}>
      <div className="flex items-center gap-2">
        <button
          ref={triggerRef}
          type="button"
          aria-haspopup="true"
          aria-expanded={open}
          className={TRIGGER_CLASS}
          onClick={() => setOpen((o) => !o)}
        >
          {label} <span aria-hidden="true">▾</span>
        </button>
        {loaded && modified && (
          <Button
            onClick={() => overwrite.mutate(loaded)}
            loading={overwrite.isPending}
            title={`Overwrite “${loaded.name}” with the list as it is now`}
          >
            Save
          </Button>
        )}
      </div>

      {overwrite.error && (
        <p role="alert" className="mt-1 text-sm text-[var(--ju-danger)]">
          {viewErrorMessage(overwrite.error)}
        </p>
      )}

      {open && (
        <div
          role="group"
          aria-label="Saved views"
          className="absolute right-0 z-20 mt-1 flex w-72 flex-col gap-2 rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg"
        >
          {unavailable ? (
            <p className="text-sm text-[var(--ju-muted)]">Views unavailable</p>
          ) : !views ? (
            <p className="text-sm text-[var(--ju-muted)]">Loading views…</p>
          ) : views.length === 0 ? (
            <p className="text-sm text-[var(--ju-muted)]">No saved views yet</p>
          ) : (
            <ul className="flex flex-col">
              {views.map((view) => (
                <li key={view.id}>
                  <button
                    type="button"
                    className={ITEM_CLASS}
                    aria-current={view.id === loaded?.id ? 'true' : undefined}
                    onClick={() => {
                      setOpen(false);
                      overwrite.reset();
                      onApply(view);
                    }}
                  >
                    {view.isDefault && (
                      <>
                        <span aria-hidden="true">★ </span>
                        <span className="sr-only">Default: </span>
                      </>
                    )}
                    {view.name}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </div>
  );
}
```

- [ ] **Step 6: Wire it into `IssueList`**

In `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`:

(a) Add imports:

```tsx
import type { SavedView } from '../../api/views';
import { ViewsMenu } from './ViewsMenu';
import { useSavedViews } from './useSavedViews';
```

(b) Replace the `useListState()` destructuring line with:

```tsx
  const {
    tree, setTree, columns, setColumns, sortBy, sortDescending, setSort,
    viewId, hasListState, applyParams, setViewId,
  } = useListState();
```

(c) Directly after the `useEffect(() => { setPage(1); }, [tree]);` block, add:

```tsx
  // What a saved view can store, as the list shows it now. Memoised so "modified" is recomputed
  // only when one of these actually changes.
  const snapshot = useMemo(
    () => ({ tree, sortBy, sortDescending, columns }),
    [tree, sortBy, sortDescending, columns],
  );
  const savedViews = useSavedViews(id, { viewId, hasListState, applyParams, setViewId }, snapshot);

  // A view is a new list: start it on page 1 even when only its sort differs from what was shown.
  const openView = (view: SavedView) => {
    setPage(1);
    savedViews.apply(view);
  };
```

(d) In the toolbar, make `ViewsMenu` the first child of `<div className="flex items-center gap-2">`, before `ColumnChooser`:

```tsx
          <ViewsMenu
            projectId={id}
            views={savedViews.views}
            unavailable={savedViews.unavailable}
            loaded={savedViews.loaded}
            modified={savedViews.modified}
            snapshot={snapshot}
            onApply={openView}
          />
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues && npm run typecheck && npm run lint`
Expected: PASS — the new file and every existing issues test (they now answer the views request with `[]`).

- [ ] **Step 8: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): Views dropdown — open a saved view, modified marker, Save" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Save as new, rename, default, delete

**Files:**
- Modify (full rewrite below): `src/JiraUnpacked.Web/src/features/issues/ViewsMenu.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx`

**Interfaces:**
- Consumes: Task 4 (`createView`, `deleteView`, `updateView`, `saveRequest`, `resaveRequest`, `withView`, `withoutView`), Task 5 (`ViewsMenuProps`, `viewErrorMessage`, `SavedViews`), Task 3 (`setViewId`).
- Produces: `ViewsMenuProps` gains `onLoad: (viewId: string | null) => void` (IssueList passes `setViewId`).
- Copy: `Save as new view…`, `Rename…`, `Set as default` / `Remove default`, `Delete…`; name field label `View name`; confirm title `Delete view “<name>”?`, confirm button `Delete view`; empty list `No saved views yet`.

- [ ] **Step 1: Write the failing tests**

In `SavedViews.test.tsx`, add `import { DEFAULT_COLUMNS } from './useListState';` to the imports and append:

```tsx
describe('saved views — managing', () => {
  it('saves the list as a new view and loads it', async () => {
    mockIssues();
    const api = mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary&sortDesc=false');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByText('No saved views yet')).toBeInTheDocument();
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), '  By summary  ');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: By summary' })).toBeInTheDocument();
    expect(api.requests).toEqual([{
      method: 'POST',
      body: { name: 'By summary', columns: DEFAULT_COLUMNS, sortBy: 'summary', sortDescending: false },
    }]);
    expect(urlParams().get('view')).toBe(api.views[0].id);
    expect(screen.queryByRole('group', { name: 'Saved views' })).not.toBeInTheDocument();
  });

  it('says a taken name is taken, keeping the menu open with the name as typed', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'My backlog');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await within(menu()).findByRole('alert')).toHaveTextContent('A view with this name already exists');
    expect(within(menu()).getByLabelText('View name')).toHaveValue('My backlog');
  });

  it('shows the server’s own reason for any other refusal, such as the per-project cap', async () => {
    mockIssues();
    mockViewsApi([]);
    server.use(http.post('/api/projects/p1/filters', () =>
      HttpResponse.json({ title: 'A project may have at most 100 saved filters.' }, { status: 409 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    await user.type(within(menu()).getByLabelText('View name'), 'One too many');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await within(menu()).findByRole('alert')).toHaveTextContent('A project may have at most 100 saved filters.');
  });

  it('needs a name: Save stays disabled while it is blank, and the field stops at 100 characters', async () => {
    mockIssues();
    mockViewsApi([]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    await user.click(within(menu()).getByRole('button', { name: 'Save as new view…' }));
    const name = within(menu()).getByLabelText('View name');
    const save = within(menu()).getByRole('button', { name: 'Save' });

    expect(save).toBeDisabled();
    await user.type(name, '   ');
    expect(save).toBeDisabled();
    expect(name).toHaveAttribute('maxlength', '100');
  });

  it('renames the loaded view without saving the edits made since opening it', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');
    await user.type(screen.getByLabelText('Value for condition 1'), 's');   // an unsaved edit

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Rename…' }));
    const name = within(menu()).getByLabelText('View name');
    expect(name).toHaveValue('My backlog');
    await user.clear(name);
    await user.type(name, 'Login bugs');
    await user.click(within(menu()).getByRole('button', { name: 'Save' }));

    expect(await screen.findByRole('button', { name: 'Views: Login bugs • modified' })).toBeInTheDocument();
    expect(api.requests).toEqual([{
      method: 'PUT', id: 'v-backlog', body: {
        name: 'Login bugs', filter: LOGIN_FILTER,
        columns: ['jiraKey', 'summary', 'priority'], sortBy: 'priority', sortDescending: false,
      },
    }]);
  });

  it('sets and removes the default, and stars the default in the list', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG, TRIAGE]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'Triage');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Set as default' }));
    await waitFor(() => expect(api.views.find((v) => v.id === 'v-triage')?.isDefault).toBe(true));
    // Only the flag moves: the view's stored definition goes back unchanged.
    expect(api.requests.at(-1)).toEqual({
      method: 'PUT', id: 'v-triage', body: { name: 'Triage', sortBy: 'createdAt', isDefault: true },
    });

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    const items = within(menu()).getAllByRole('listitem');
    expect(items[0]).toHaveTextContent('★ Default: Triage');
    expect(items[1]).toHaveTextContent('My backlog');
    expect(items[1]).not.toHaveTextContent('★');

    await user.click(within(menu()).getByRole('button', { name: 'Remove default' }));
    await waitFor(() => expect(api.views.some((v) => v.isDefault)).toBe(false));
    expect(api.requests.at(-1)?.body).toEqual({ name: 'Triage', sortBy: 'createdAt', isDefault: false });
  });

  it('deletes the loaded view after confirming, and leaves the list as it is', async () => {
    mockIssues();
    const api = mockViewsApi([BACKLOG]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Delete…' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete view “My backlog”?' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete view' }));

    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(api.views).toEqual([]);
    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('priority');
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('login');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('keeps the confirmation open with the reason when a delete fails', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    server.use(http.delete('/api/projects/p1/filters/:viewId', () => new HttpResponse(null, { status: 500 })));
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');
    await openView(user, 'My backlog');

    await user.click(screen.getByRole('button', { name: /^Views/ }));
    await user.click(within(menu()).getByRole('button', { name: 'Delete…' }));
    const dialog = screen.getByRole('dialog', { name: 'Delete view “My backlog”?' });
    await user.click(within(dialog).getByRole('button', { name: 'Delete view' }));

    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Something went wrong on the server.');
    expect(urlParams().get('view')).toBe('v-backlog');
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/SavedViews.test.tsx`
Expected: the new `managing` tests FAIL (no `Save as new view…`); Task 5's tests still PASS.

- [ ] **Step 2: Rewrite `ViewsMenu.tsx`**

Replace the whole file with:

```tsx
import { useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '../../api/errors';
import { createView, deleteView, updateView, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import { Button, ConfirmDialog, Field } from '../../ui';
import { INPUT_CLASS } from './styles';
import { resaveRequest, saveRequest, withView, withoutView, type ListSnapshot } from './viewState';

/** The server's cap on a name. */
const MAX_NAME_LENGTH = 100;

const TRIGGER_CLASS =
  'rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]';
const ITEM_CLASS =
  'w-full rounded px-2 py-1 text-left text-sm text-[var(--ju-text)] hover:bg-[var(--ju-surface-2)] ' +
  'disabled:cursor-not-allowed disabled:opacity-60';

/** The server's own words for a refusal, except the clash the user fixes by typing: a taken name. */
export function viewErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.status === 409 && /already exists/i.test(error.title)
      ? 'A view with this name already exists'
      : error.title;
  }
  return 'Could not save the view.';
}

export interface ViewsMenuProps {
  projectId: string;
  views: SavedView[] | undefined;
  unavailable: boolean;
  loaded: SavedView | undefined;
  modified: boolean;
  /** The list as it is now — what Save and Save as new write. */
  snapshot: ListSnapshot;
  onApply: (view: SavedView) => void;
  /** Points the URL at a view (after Save as new) or at none (after Delete). */
  onLoad: (viewId: string | null) => void;
}

type Mode = 'list' | 'saveAs' | 'rename';

interface NameFormProps {
  initial: string;
  pending: boolean;
  onSubmit: (name: string) => void;
  onCancel: () => void;
}

/** The name field Save as new and Rename share: required, trimmed, at most 100 characters. */
function NameForm({ initial, pending, onSubmit, onCancel }: NameFormProps) {
  const [name, setName] = useState(initial);
  const id = useId();
  const trimmed = name.trim();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (trimmed !== '') onSubmit(trimmed);
  };

  return (
    <form className="flex flex-col gap-2" onSubmit={submit}>
      <Field label="View name" id={id}>
        <input
          className={INPUT_CLASS}
          value={name}
          maxLength={MAX_NAME_LENGTH}
          // The field appears because the user just asked to name something.
          autoFocus
          onChange={(e) => setName(e.target.value)}
        />
      </Field>
      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={trimmed === ''} loading={pending}>
          Save
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={onCancel}>
          Cancel
        </Button>
      </div>
    </form>
  );
}

/**
 * The toolbar's saved views: a button naming the loaded view (and whether the list has moved away
 * from it), Save to overwrite it, and a popover to open, create, rename, star and delete views.
 *
 * A controlled popover rather than a native <details> (ColumnChooser's choice): choosing a view or
 * finishing an action must close it, and a failed save must keep it open with the name as typed.
 */
export function ViewsMenu({
  projectId, views, unavailable, loaded, modified, snapshot, onApply, onLoad,
}: ViewsMenuProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [mode, setMode] = useState<Mode>('list');
  const [error, setError] = useState<string | null>(null);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);

  const close = () => {
    setOpen(false);
    setMode('list');
    setError(null);
  };
  const switchTo = (next: Mode) => {
    setError(null);
    setMode(next);
  };

  // A click anywhere outside closes it, like every other popover.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current?.contains(event.target as Node)) return;
      setOpen(false);
      setMode('list');
      setError(null);
    };
    document.addEventListener('mousedown', onPointerDown);
    return () => document.removeEventListener('mousedown', onPointerDown);
  }, [open]);

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (event.key !== 'Escape' || !open) return;
    event.preventDefault();
    close();
    triggerRef.current?.focus();
  };

  /** Puts a saved view into the cached list at once (so the label follows), then asks the server. */
  const store = (saved: SavedView) => {
    queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withView(old ?? [], saved));
    void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
  };

  // Overwrites with the list as it is now; isDefault left out, so the flag is unchanged.
  const overwrite = useMutation({
    mutationFn: (view: SavedView) => updateView(projectId, view.id, saveRequest(view.name, snapshot)),
    onSuccess: store,
  });

  const create = useMutation({
    mutationFn: (name: string) => createView(projectId, saveRequest(name, snapshot)),
    onSuccess: (saved) => {
      // Cached before the URL names it, so the stale-id check never sees an unknown id.
      store(saved);
      onLoad(saved.id);
      close();
    },
    onError: (e) => setError(viewErrorMessage(e)),
  });

  // Rename and the default flag re-send what the view stores, never the list's current edits.
  const edit = useMutation({
    mutationFn: ({ view, changes }: { view: SavedView; changes: { name?: string; isDefault?: boolean } }) =>
      updateView(projectId, view.id, resaveRequest(view, changes)),
    onSuccess: (saved) => {
      store(saved);
      close();
    },
    onError: (e) => setError(viewErrorMessage(e)),
  });

  const remove = useMutation({
    mutationFn: (view: SavedView) => deleteView(projectId, view.id),
    onSuccess: (_nothing, view) => {
      queryClient.setQueryData<SavedView[]>(queryKeys.views(projectId), (old) => withoutView(old ?? [], view.id));
      void queryClient.invalidateQueries({ queryKey: queryKeys.views(projectId) });
      setConfirmingDelete(false);
      // The list keeps showing what it shows; it is just no longer "that view".
      onLoad(null);
    },
  });

  const label = loaded ? `Views: ${loaded.name}${modified ? ' • modified' : ''}` : 'Views';

  return (
    <>
      <div ref={rootRef} className="relative" onKeyDown={onKeyDown}>
        <div className="flex items-center gap-2">
          <button
            ref={triggerRef}
            type="button"
            aria-haspopup="true"
            aria-expanded={open}
            className={TRIGGER_CLASS}
            onClick={() => (open ? close() : setOpen(true))}
          >
            {label} <span aria-hidden="true">▾</span>
          </button>
          {loaded && modified && (
            <Button
              onClick={() => overwrite.mutate(loaded)}
              loading={overwrite.isPending}
              title={`Overwrite “${loaded.name}” with the list as it is now`}
            >
              Save
            </Button>
          )}
        </div>

        {overwrite.error && (
          <p role="alert" className="mt-1 text-sm text-[var(--ju-danger)]">
            {viewErrorMessage(overwrite.error)}
          </p>
        )}

        {open && (
          <div
            role="group"
            aria-label="Saved views"
            className="absolute right-0 z-20 mt-1 flex w-72 flex-col gap-2 rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg"
          >
            {unavailable ? (
              <p className="text-sm text-[var(--ju-muted)]">Views unavailable</p>
            ) : (
              <>
                {!views ? (
                  <p className="text-sm text-[var(--ju-muted)]">Loading views…</p>
                ) : views.length === 0 ? (
                  <p className="text-sm text-[var(--ju-muted)]">No saved views yet</p>
                ) : (
                  <ul className="flex flex-col">
                    {views.map((view) => (
                      <li key={view.id}>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          aria-current={view.id === loaded?.id ? 'true' : undefined}
                          onClick={() => {
                            close();
                            overwrite.reset();
                            onApply(view);
                          }}
                        >
                          {view.isDefault && (
                            <>
                              <span aria-hidden="true">★ </span>
                              <span className="sr-only">Default: </span>
                            </>
                          )}
                          {view.name}
                        </button>
                      </li>
                    ))}
                  </ul>
                )}

                <hr className="border-[var(--ju-border)]" />

                {mode === 'list' && (
                  <div className="flex flex-col">
                    <button type="button" className={ITEM_CLASS} onClick={() => switchTo('saveAs')}>
                      Save as new view…
                    </button>
                    {loaded && (
                      <>
                        <button type="button" className={ITEM_CLASS} onClick={() => switchTo('rename')}>
                          Rename…
                        </button>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          disabled={edit.isPending}
                          onClick={() => {
                            setError(null);
                            edit.mutate({ view: loaded, changes: { isDefault: !loaded.isDefault } });
                          }}
                        >
                          {loaded.isDefault ? 'Remove default' : 'Set as default'}
                        </button>
                        <button
                          type="button"
                          className={ITEM_CLASS}
                          onClick={() => {
                            close();
                            remove.reset();
                            setConfirmingDelete(true);
                          }}
                        >
                          Delete…
                        </button>
                      </>
                    )}
                  </div>
                )}

                {mode === 'saveAs' && (
                  <NameForm
                    initial=""
                    pending={create.isPending}
                    onSubmit={(name) => {
                      setError(null);
                      create.mutate(name);
                    }}
                    onCancel={() => switchTo('list')}
                  />
                )}

                {mode === 'rename' && loaded && (
                  <NameForm
                    initial={loaded.name}
                    pending={edit.isPending}
                    onSubmit={(name) => {
                      setError(null);
                      edit.mutate({ view: loaded, changes: { name } });
                    }}
                    onCancel={() => switchTo('list')}
                  />
                )}

                {error && (
                  <p role="alert" className="text-sm text-[var(--ju-danger)]">
                    {error}
                  </p>
                )}
              </>
            )}
          </div>
        )}
      </div>

      {loaded && (
        <ConfirmDialog
          open={confirmingDelete}
          title={`Delete view “${loaded.name}”?`}
          confirmLabel="Delete view"
          onConfirm={() => remove.mutate(loaded)}
          onCancel={() => setConfirmingDelete(false)}
          pending={remove.isPending}
          error={remove.error ? viewErrorMessage(remove.error) : null}
        >
          “{loaded.name}” is removed for good. The list keeps showing what it shows now.
        </ConfirmDialog>
      )}
    </>
  );
}
```

- [ ] **Step 3: Pass `onLoad` from `IssueList`**

In `IssueList.tsx`, add to the `<ViewsMenu … />` props:

```tsx
            onLoad={setViewId}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues && npm run typecheck && npm run lint`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): save as new, rename, set default and delete saved views" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Default view on a bare URL; stale view ids

**Files:**
- Modify (full rewrite below): `src/JiraUnpacked.Web/src/features/issues/useSavedViews.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.tsx`
- Test: `src/JiraUnpacked.Web/src/features/issues/SavedViews.test.tsx`

**Interfaces:**
- Consumes: Task 3 (`hasListState`, `setViewId`, `applyParams` with `replace: true`), Task 4 (`viewParams`), Task 5 (`ViewListControls`, `SavedViews`).
- Produces: `SavedViews.holdQuery: boolean` — true only while a list opened on a bare URL waits to learn whether a default view opens instead. `IssueList`'s issue query is `enabled: !catalogPending && !savedViews.holdQuery`.

- [ ] **Step 1: Write the failing tests**

In `SavedViews.test.tsx`, change the msw import to `import { delay, http, HttpResponse } from 'msw';` and append:

```tsx
describe('saved views — the default view and stale links', () => {
  it('opens the default view on a bare URL, replacing the entry rather than adding one', async () => {
    const bodies = mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }, TRIAGE]);
    const user = userEvent.setup();
    renderList('/elsewhere?from=before', '/projects/p1/issues');

    expect(await screen.findByRole('button', { name: 'Views: My backlog' })).toBeInTheDocument();
    expect(urlParams().get('view')).toBe('v-backlog');
    // The very first request is already the default view's — no flash of the unfiltered list.
    expect(bodies[0]).toMatchObject({ filter: LOGIN_FILTER, sortBy: 'priority', sortDescending: false });

    await user.click(screen.getByRole('button', { name: 'Go back' }));

    expect(await screen.findByText('Somewhere else')).toBeInTheDocument();
    expect(urlParams().get('from')).toBe('before');
  });

  it('opens the default only once: Clear all and a later bare URL leave the list bare', async () => {
    mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }]);
    const user = userEvent.setup();
    renderList();
    await screen.findByRole('button', { name: 'Views: My backlog' });

    await user.click(screen.getByRole('button', { name: 'Clear all' }));
    expect(await screen.findByRole('button', { name: 'Views: My backlog • modified' })).toBeInTheDocument();
    expect(urlParams().has('filter')).toBe(false);

    await user.click(screen.getByRole('button', { name: 'Open the bare list' }));

    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(urlParams().toString()).toBe('');
  });

  it('leaves a URL that already says what to show alone, and does not wait for the views to send it', async () => {
    const bodies = mockIssues();
    server.use(http.get('/api/projects/p1/filters', async () => {
      await delay('infinite');
      return HttpResponse.json([]);
    }));
    renderList('/projects/p1/issues?sortBy=summary');

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    expect(bodies[0]).toMatchObject({ sortBy: 'summary' });
  });

  it('does not open the default over list state in the URL', async () => {
    mockIssues();
    mockViewsApi([{ ...BACKLOG, isDefault: true }]);
    const user = userEvent.setup();
    renderList('/projects/p1/issues?sortBy=summary');
    await screen.findByText('ACME-1');

    await user.click(screen.getByRole('button', { name: 'Views' }));
    expect(await within(menu()).findByRole('button', { name: /My backlog/ })).toBeInTheDocument();
    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('summary');
  });

  it('on a bare URL, sends nothing until the views are known', async () => {
    const bodies = mockIssues();
    let catalogServed = false;
    server.use(
      http.get('/api/filter-catalog', () => {
        catalogServed = true;
        return HttpResponse.json(FILTER_CATALOG);
      }),
      http.get('/api/projects/p1/filters', async () => {
        await delay('infinite');
        return HttpResponse.json([]);
      }),
    );
    renderList();

    await waitFor(() => expect(catalogServed).toBe(true));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(bodies).toEqual([]);
  });

  it('on a bare URL, goes ahead unfiltered when the views cannot load', async () => {
    const bodies = mockIssues();
    server.use(http.get('/api/projects/p1/filters', () => new HttpResponse(null, { status: 500 })));
    renderList();

    expect(await screen.findByText('ACME-1')).toBeInTheDocument();
    expect(bodies[0]).not.toHaveProperty('filter');
  });

  it('lets list state that arrives before the views win over the default', async () => {
    mockIssues();
    server.use(http.get('/api/projects/p1/filters', async () => {
      await delay(150);
      return HttpResponse.json([{ ...BACKLOG, isDefault: true }]);
    }));
    const user = userEvent.setup();
    renderList();

    await user.click(screen.getByRole('button', { name: 'Open the list sorted by summary' }));
    await screen.findByText('ACME-1');
    await new Promise((resolve) => setTimeout(resolve, 250));

    expect(urlParams().has('view')).toBe(false);
    expect(urlParams().get('sortBy')).toBe('summary');
  });

  it('drops a view id that names no view, and keeps the rest of the link', async () => {
    mockIssues();
    mockViewsApi([BACKLOG]);
    const filter = encodeURIComponent('{"logic":"And","conditions":[{"field":"summary","op":"Contains","value":"crash"}]}');
    renderList(`/projects/p1/issues?view=gone&sortBy=summary&filter=${filter}`);

    await waitFor(() => expect(urlParams().has('view')).toBe(false));
    expect(urlParams().get('sortBy')).toBe('summary');
    expect(await screen.findByRole('button', { name: 'Views' })).toBeInTheDocument();
    expect(screen.getByLabelText('Value for condition 1')).toHaveValue('crash');
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/SavedViews.test.tsx`
Expected: the new tests FAIL (no default applied; the stale `view=gone` stays; the bare-URL request is sent before the views arrive).

- [ ] **Step 2: Rewrite `useSavedViews.ts`**

Replace the whole file with:

```ts
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { listViews, type SavedView } from '../../api/views';
import { queryKeys } from '../../api/queryKeys';
import type { ListParams } from './useListState';
import { isModified, viewParams, type ListSnapshot } from './viewState';

/** The parts of useListState that saved views drive. */
export interface ViewListControls {
  viewId: string | null;
  hasListState: boolean;
  applyParams: (next: ListParams, options: { replace: boolean }) => void;
  setViewId: (id: string | null) => void;
}

export interface SavedViews {
  /** Undefined until loaded (or when the request failed). */
  views: SavedView[] | undefined;
  /** The request failed: the menu says so and the list carries on without views. */
  unavailable: boolean;
  /** The view named by `view=`, when it is in the list. */
  loaded: SavedView | undefined;
  /** The list differs from the loaded view in something Save would write. */
  modified: boolean;
  /** Opens a view: its whole URL, pushed, so Back returns to what the list showed. */
  apply: (view: SavedView) => void;
  /**
   * True while a list opened on a bare URL waits to learn whether a default view opens instead.
   * The issue query waits on it, so the first request is the default view's rather than a flash of
   * the unfiltered list. Never true for a URL that already says what to show.
   */
  holdQuery: boolean;
}

/** The project's saved views, which one the list shows, and the default view on a bare URL. */
export function useSavedViews(projectId: string, list: ViewListControls, snapshot: ListSnapshot): SavedViews {
  const { viewId, hasListState, applyParams, setViewId } = list;
  const query = useQuery({ queryKey: queryKeys.views(projectId), queryFn: () => listViews(projectId) });
  const views = query.data;

  const loaded = viewId ? views?.find((v) => v.id === viewId) : undefined;
  const modified = useMemo(() => (loaded ? isModified(loaded, snapshot) : false), [loaded, snapshot]);

  const apply = useCallback(
    (view: SavedView) => applyParams(viewParams(view), { replace: false }),
    [applyParams],
  );

  // Decided once, from the URL the list opened with: a bare URL is the only case in which a
  // default view may step in. Clear all, or coming back to a bare URL later, never re-applies it.
  const [bareAtMount] = useState(() => !hasListState);
  const [defaultSettled, setDefaultSettled] = useState(!bareAtMount);
  const defaultRequested = useRef<string | null>(null);

  useEffect(() => {
    if (defaultSettled) return;
    if (defaultRequested.current !== null) {
      // Applied; the query may go once the URL carries it (the tree is adopted in the same render).
      if (viewId === defaultRequested.current) setDefaultSettled(true);
      return;
    }
    // List state that arrived before the views (a link followed while they loaded) wins; and a
    // failed views request means no default — the list proceeds unfiltered.
    if (hasListState || query.isError) {
      setDefaultSettled(true);
      return;
    }
    if (!views) return;
    const fallback = views.find((v) => v.isDefault);
    if (!fallback) {
      setDefaultSettled(true);
      return;
    }
    defaultRequested.current = fallback.id;
    // Replace: the bare URL it stands in for is not somewhere Back should return to.
    applyParams(viewParams(fallback), { replace: true });
  }, [defaultSettled, viewId, hasListState, query.isError, views, applyParams]);

  // A view id that names no view — deleted elsewhere, or from someone else's link — is dropped
  // silently; the rest of the URL still applies. Not while a refetch may still bring it.
  useEffect(() => {
    if (viewId !== null && views && !query.isFetching && !views.some((v) => v.id === viewId)) {
      setViewId(null);
    }
  }, [viewId, views, query.isFetching, setViewId]);

  return { views, unavailable: query.isError, loaded, modified, apply, holdQuery: !defaultSettled };
}
```

- [ ] **Step 3: Gate the issue query in `IssueList`**

In `IssueList.tsx`, in the issues `useQuery`, replace `enabled: !catalogPending,` with:

```tsx
    // Also waits, on a bare URL only, until the saved views say whether a default view opens
    // instead — otherwise the first request would flash the unfiltered list (see useSavedViews).
    enabled: !catalogPending && !savedViews.holdQuery,
```

`savedViews` is declared (Task 5) above this query; if it is not, move the `snapshot`/`savedViews` block above the `useQuery` call.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues && npm run typecheck && npm run lint`
Expected: PASS — including the Task 5/6 tests (their fixtures have no default on a bare URL) and every pre-existing issues test (bare-URL ones now wait for the `[]` views response, then run as before).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): open the default view on a bare list; drop stale view ids" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final verification (after Task 7, before handing back)

- [ ] Migration applies from zero: `docker exec jiraunpacked-db psql -U jiraunpacked -d postgres -c "DROP DATABASE IF EXISTS ju_test_views WITH (FORCE)"` (the suite's `MigrateAsync` recreates it and applies every migration, `AddSavedFilterDefault` included).
- [ ] `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_views dotnet test JiraUnpacked.sln -v q` → PASS (FR-B2 gate included).
- [ ] `cd C:/jw-filters && dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api` → no changes.
- [ ] `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build` → PASS.
- [ ] `git -C C:/jw-filters diff feat/filter-builder-v2 -- src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json` → empty (no dependency changes).
- [ ] `git -C C:/jw-filters grep -n "localStorage" -- src/JiraUnpacked.Web/src/features` → no new hits.
- [ ] `git -C C:/jw-filters status` clean; `git -C C:/jw-filters log --oneline feat/filter-builder-v2..` shows the spec commit, this plan's commit and seven task commits. Nothing pushed.

---

## Self-Review

**Spec coverage.**
§1 Data — `columns`/`sortBy`/`sortDescending` in `DefinitionJson`, absent = default: Task 1; `IsDefault` + partial unique index, one migration, RLS unchanged: Task 2.
§2 API — view/request fields: Tasks 1–2; validation (known sort, ≤20 known columns, no duplicates, compiler, disabled parts): Task 1; default handling (clear first in one transaction, 409 on race, `false` clears, null unchanged/false): Task 2; list order: Task 2.
§3 SPA toolbar — label/`• modified`/Save visibility and overwrite: Task 5; menu with ★, apply, separator, Save as new, Rename, Set/Remove default, Delete with `ConfirmDialog`, Escape: Tasks 5–6; name rules, 409 message, cap message: Task 6.
§3 URL and state — `view=<id>`, push on apply with page 1, defaults absent: Tasks 3–5; modified = normalised comparison: Task 4 (`isModified`) + Task 5; `view` kept through edits and Clear all: Task 3 + Task 7 test; unknown id dropped silently: Task 7; what is saved (`toSavable`, empty tree = no filter, columns/sort as shown): Task 4.
§3 Default view — bare-URL detection incl. `view`/`logic`, replace, once per mount, query waits only then, proceeds on failure: Tasks 3 and 7.
§4 Error handling — views unavailable: Task 5; save/rename failures in the open menu with input intact: Task 6 (delete failures in the confirm dialog, toolbar Save failures under the toolbar — see Decisions); unknown catalog field applied as-is: unchanged sub-project 1 behaviour (`viewTree` keeps the row; `pruneTree` drops it from the query).
§5 Testing — every listed server case is in Tasks 1–2 (FR-B2 gate run in Task 2 and Final verification; migration from zero in Final verification); every listed web case is in Tasks 3–7.

**Placeholder scan.** No TBD/TODO; every code step shows the code; edits to existing files name the block they replace or where they go; the migration is generated and its expected content is spelled out for checking.

**Type consistency.** `ListParams`/`LIST_PARAMS`/`applyParams(next, { replace })`/`setViewId`/`viewId`/`hasListState` (Task 3) are what `viewState.viewParams` (4), `ViewListControls` (5, 7) and `IssueList` (5) use. `SavedView`/`SaveViewRequest` (4) match the server's `SavedFilterView`/`SaveFilterRequest` JSON after Task 2 (`isDefault` non-null, the rest nullable). `ListSnapshot` (4) is built in `IssueList` (5) and consumed by `useSavedViews`/`ViewsMenu`. `ViewsMenuProps` gains only `onLoad` in Task 6; `SavedViews` gains only `holdQuery` in Task 7. Server: `SaveFilterRequest`'s new members are optional trailing parameters, so no existing construction breaks; `SavedFilterView` is only constructed in `ToView`.

**Review Focus.** Five lines above, each with its pinning test named in the owning task (4, 2, 2, 1, 4/5).
