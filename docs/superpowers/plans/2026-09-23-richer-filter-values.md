# Richer Filter Values (Relative Dates, Between, Sprint Smart Values, Status Category) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Filters that stay true over time — "updated in the last 14 days", "created this month", "in the active sprint", "not done" — evaluated by the server at query time, so a saved view, a CSV export and an API caller all get the same answer next month.

**Architecture:** Four operators are appended to `FilterOperator` and translated in `IssueFilterTranslator`, which now takes the injected `IClock`; the calendar arithmetic lives in a pure `RelativeDates` helper (now and zone are arguments). Smart sprint values and status categories resolve to id sets through the SAME tenant-scoped `DictNameResolver` the names use, via three closed lookup keys (`FilterLookups`), so the issue query never joins. The SPA gains object-shaped values (`{amount, unit}`, `{period, timeZone}`, `{smart}`) in its wire types, a reader that canonicalises them from links and stored views, three new value controls, a Smart section in the sprint pickers and a "Category" column.

**Tech Stack:** .NET 8, EF Core 8 + Npgsql, xUnit; React 19, react-router-dom 7, @tanstack/react-query 5, Vitest + Testing Library + user-event 14 + MSW 2, Tailwind 4.

**Spec:** `docs/superpowers/specs/2026-09-23-richer-filter-values-design.md`

## Global Constraints

- **Work only in the worktree `C:/jw-filters`** (branch `feat/richer-filter-values`, based on `origin/develop`, which already has filter builder v2 and saved views). Never read or write `C:\Users\AliaksandrHubanau\Private\Claude\JiraUnpacked` — other sessions use that checkout. Do not push.
- **Integration tests use their own database:** every `dotnet test` run sets `JU_TEST_DATABASE=ju_test_richer` (Postgres = the `jiraunpacked-db` container on `localhost:5432`, already running). Bash: `JU_TEST_DATABASE=ju_test_richer dotnet test …`. PowerShell: `$env:JU_TEST_DATABASE='ju_test_richer'; dotnet test …`. The suite never truncates; a shared database collides with parallel branches.
- **Web setup:** `node_modules` already exists in `src/JiraUnpacked.Web`; if it is missing, run `npm ci` there once.
- SPA keeps **exactly four runtime deps**: `react`, `react-dom`, `react-router-dom`, `@tanstack/react-query`. **No new dependencies of any kind** (runtime or dev). No time-zone library: the SPA's zone comes from `Intl.DateTimeFormat().resolvedOptions().timeZone`; the server uses `TimeZoneInfo.FindSystemTimeZoneById`.
- Everything is evaluated **on the server at query time** (the translator, with the injected `IClock`), never pre-computed by the SPA.
- `FilterOperator` gains `Between`, `InLast`, `NotInLast`, `InPeriod` **appended** after `NotContains`. Enums stay serialised by name.
- `Between`: fields `createdAt`, `updatedAt`, `storyPoints`; `values: [from, to]`; either bound may be `null` (open), not both; a date-only `to` (`2026-09-30`) includes that whole day (`x < to + 1 day`), a full timestamp is used as-is; dates are UTC; `from > to` → 400. storyPoints: both bounds inclusive, decimal bounds.
- `InLast` / `NotInLast`: fields `createdAt`, `updatedAt`; `value: { "amount": 14, "unit": "days" }`; `x ≥ now − amount·unit` / `x < now − amount·unit`; `unit` ∈ `hours, days, weeks, months`; `amount` integer 1…3650; months use `AddMonths`.
- `InPeriod`: fields `createdAt`, `updatedAt`; `value: { "period": "thisWeek", "timeZone": "Europe/Warsaw" }`; `start ≤ x < end`; `period` ∈ `today, yesterday, thisWeek, lastWeek, thisMonth, lastMonth, thisQuarter, lastQuarter, thisYear, lastYear`; weeks start Monday (ISO 8601); `timeZone` is an IANA id; missing → UTC; unknown id → 400.
- Smart sprint values, wire `{ "smart": "<key>" }` — an **object, never a string**: `active` "Active sprint" (state `active`), `future` "Future sprints" (`future`), `open` "Open sprints (active + future)" (`active` or `future`), `closed` "Closed sprints" (`closed`). Inside `values` for `In`/`NotIn`, as `value` for `Eq`/`Neq`. Strings keep meaning sprint names; `null` keeps meaning (Empty). Valid only on `sprint`, elsewhere → 400; unknown key → 400; resolving to no sprint matches nothing (not an error). `GET filter-values?field=sprint` adds `smart: [{ key, label, count }]`; other fields omit `smart`.
- New field **`statusCategory`** (catalog type `StatusCategory`): operators `Eq, Neq, In, NotIn, IsNull, IsNotNull` with sub-project 1's empty rule; Empty = no status, or a status with no category; names → status ids → `StatusId in set` (no join); column `statusCategory`, header **"Category"**, sortable; the issue projection and the CSV export carry it.
- Operator labels, verbatim (appended to the label map): `Between` between · `InLast` in the last · `NotInLast` more than … ago · `InPeriod` in period.
- SPA value controls: `Between` = two date inputs (dates) or two number inputs (storyPoints), either may be blank; `InLast`/`NotInLast` = number input + unit select (hours / days / weeks / months), default `14 days`, `NotInLast` reads "more than [14] [days] ago"; `InPeriod` = select with the ten periods in reading order (Today … Last year), `timeZone` set silently from the browser when the operator is chosen and shown as a small muted hint next to the select; sprint pickers (single and multi) show a **Smart** section above the names; chips show the label.
- Completeness: `Between` needs at least one bound; `InLast`/`NotInLast` need an amount ≥ 1; `InPeriod` needs a period.
- **No schema change, no migration.** Object values live only in request JSON and the existing `SavedFilters.DefinitionJson` jsonb.
- Filter input never reaches a SQL string: closed catalogs + Expression trees. All reads through the injected tenant-scoped `AppDbContext`. No `IgnoreQueryFilters`, no `FromSql`.
- `src/ui/` imports neither `src/api/` nor `src/auth/`; new modules live in `src/features/issues/`. Colours only through `--ju-*` CSS variables. Only `ju.username` and `ju.theme` may go to `localStorage`.
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` (second `-m`).

## Review Focus

1. **The production image has no time-zone data.** `mcr.microsoft.com/dotnet/aspnet:8.0-alpine` ships no `/usr/share/zoneinfo` (checked: `Europe/Warsaw` → `TimeZoneNotFoundException`, only `UTC` resolves), so every "in period" view saved from a browser outside UTC would 400 in Docker while every developer machine and CI runner passes. Expected: the same view works in the container. Pinned by Task 1 (`RUN apk add --no-cache tzdata` + the runtime-image check step) and Final verification's image check.
2. **A period boundary that is not an ordinary midnight** — a zone that springs forward AT midnight (00:00 never exists that day), or a month that starts in summer time and ends in winter time. Expected: correct UTC instants, never an `ArgumentException` → 500. Pinned by Task 1 (`A_midnight_skipped_by_dst_starts_the_day_at_the_first_time_that_exists`, `A_period_that_spans_a_dst_change_uses_each_ends_own_offset`).
3. **A sprint someone actually named "active" (or "open").** Expected: the string `"active"` still means that sprint; only `{ "smart": "active" }` means the state. Pinned by Task 3 (`A_string_still_means_a_sprint_name` integration, `A_string_active_is_a_sprint_name_not_a_state` unit).
4. **A link or stored view whose object values are not exactly what the editor writes** — the API's explicit nulls, keys in another order, extra keys, or bad shapes (`{"amount":"14"}`, `{"period":"fortnight"}`, `{"smart":"current"}`). Expected: good values are read in canonical form (so the view does not read as "modified"), bad ones leave the row visible but unfinished (never sent, never a 400 loop, never a crash). Pinned by Task 5 (`does not call a view with relative and smart values modified…` in `viewState.test.ts`, the `object values` block in `filterTree.test.ts`).
5. **A time-zone id that is a path or a Windows name** (`../../etc/passwd`, `Central European Standard Time`). Expected: a 400 on every host — never a file read on Linux, never an answer that differs between a Windows dev box and the Linux container. Pinned by Task 1 (`Anything_else_is_refused_on_every_host`) and Task 2 (`InPeriod_refuses_a_time_zone_that_is_not_an_iana_id_this_host_has`, `A_bad_relative_value_is_a_400_naming_the_problem`).

## Decisions this plan makes where the spec is silent

- **"Per project" resolution without a new parameter.** `IIssueFilterCompiler.CompileAsync` has no project id (the saved-view validator calls it without one). Smart values and categories resolve tenant-wide through the tenant-scoped context, and every caller then restricts issues to one project (`i.JiraProjectId == projectId`). An issue's sprint row is always its own project's (derivation creates sprints per project) and its status is always its own connection's, so the matched rows are exactly the per-project answer.
- **Lookup keys, not a new resolver type.** `DictNameResolver` keeps its signature; its `field` argument may also be one of three closed constants in `FilterLookups` (`sprint.state`, `statusCategory`, `statusCategory.none`). Nothing a caller sends ever becomes one of them.
- **`InPeriod` starts at `thisWeek`**, the way `InLast` starts at 14 days: choosing the operator already filters. The select shows "— choose —" only for a condition whose period is missing or unknown (from a link), which is then unfinished.
- **Time-zone ids must be IANA-shaped** (`^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}\z`, ≤ 64 chars) before they reach the OS. Windows display ids are refused everywhere, because they resolve on Windows only. `UTC` and `Etc/…` are accepted. An explicit `"timeZone": null` means UTC, like a missing one.
- **Docker runtime installs `tzdata`** (the only image change).
- **Midnight that does not exist** (DST jump at 00:00) starts the day at the first local time that does (checked in 15-minute steps). An ambiguous midnight takes standard time (`ConvertTimeToUtc`'s rule).
- **`amount` must be a JSON integer** (`14`, not `14.5` or `"14"`); the SPA truncates what is typed. The SPA's completeness also requires an integer, so a hand-edited fractional amount stays unfinished rather than earning a 400.
- **`Between` needs exactly two elements** in `values`. Only the `to` bound gets the date-only rule; a date-only `from` is that day's midnight UTC, its natural meaning. `from > to` is judged against the effective end (a date-only `to` of the same day as a timestamp `from` is fine).
- **Status categories are shown as stored** (Jira's category *names*, e.g. `To Do`). A blank category string counts as no category, like `null`, in both the filter and the value list. Category names match case-sensitively, like every other dictionary name.
- **Smart keys are case-sensitive**; sprint states are compared exactly as Jira sends them (lower case).
- **Operator reading order:** `Eq, Neq, In, NotIn, Lt, Lte, Gt, Gte, Between, InLast, NotInLast, InPeriod, Contains, NotContains, StartsWith, IsNull, IsNotNull` — so a date field still starts at `<` when chosen.
- **Category column** sits right after Status in the table, the column chooser and the CSV (`ExportColumns` mirrors `ISSUE_COLUMNS`); it is **not** a default column, so existing links and views keep their meaning.
- **SPA labels for smart values come from the SPA's own map** (`SMART_SPRINT_LABELS`), so a chip from a link reads right before the value list loads; the server's `label` is there for other API callers. Unknown smart keys from a newer server are skipped.
- **(Empty) stays first** in both pickers, then the Smart section, then the names.
- **Report caches:** metrics and gap-checks cache by (watermark, filter JSON) with a 60 s TTL, so a relative filter there can lag the clock by up to a minute. Accepted, unchanged.

---

## File Structure

| File | Responsibility |
|---|---|
| `Dockerfile` | runtime stage installs `tzdata` |
| `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs` | + `Between`, `InLast`, `NotInLast`, `InPeriod` |
| `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs` | + `SmartValueCount`; `FilterValuesResult.Smart` |
| `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs` | `IssueListItem.StatusCategory` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/RelativeDates.cs` | **new** — pure: now − amount·unit, calendar periods in a zone, zone resolution |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SmartSprints.cs` | **new** — the four smart keys, labels, states |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs` | new ops per field; `statusCategory`; `FieldType.StatusCategory` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` | `IClock`; Between/InLast/NotInLast/InPeriod; smart values; status category; `FilterLookups` |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs` | resolver arms for the three lookup keys |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs` | display order with the new ops |
| `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SortFields.cs` | + `statusCategory` |
| `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs` | `statusCategory` values; sprint smart counts |
| `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs` | projection + sort for `StatusCategory` |
| `src/JiraUnpacked.Infrastructure/Reporting/ExportColumns.cs` | + `Category` |
| `src/JiraUnpacked.Api/Controllers/IssuesController.cs` | `filter-values` returns `smart` for sprint |
| `tests/JiraUnpacked.Tests/Reporting/RelativeDatesTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Reporting/DateOperatorTranslationTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Reporting/SmartSprintFilterTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Reporting/StatusCategoryFilterTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs` | **new** — fixed-clock host, raw seeding, query helpers |
| `tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs` | **new** |
| `tests/JiraUnpacked.Tests/Integration/StatusCategoryTests.cs` | **new** |
| `src/JiraUnpacked.Web/src/api/issues.ts` | wire types for the new ops, object values, `statusCategory`, `smart` |
| `src/JiraUnpacked.Web/src/features/issues/valueShapes.ts` | **new** — shapes, vocabularies, labels, defaults, readers, browser zone |
| `src/JiraUnpacked.Web/src/features/issues/filterTree.ts` | arity helpers, completeness, wire form, canonical parse, `freshCondition`, `switchOperator` |
| `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts` | labels; `StatusCategory` is a dictionary type |
| `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` | Between / amount+unit / period controls; smart wiring |
| `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` | smart values as chips; Smart section in both pickers |
| `src/JiraUnpacked.Web/src/features/issues/columns.ts` | + Category column |
| `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts` | catalog as the server now serves it |

---

### Task 1: Relative-date arithmetic, time zones, and zone data in the image (server, pure)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/RelativeDates.cs`
- Create: `tests/JiraUnpacked.Tests/Reporting/RelativeDatesTests.cs`
- Modify: `Dockerfile` (runtime stage)

**Interfaces:**
- Produces (all `internal static` on `internal static class RelativeDates`, namespace `JiraUnpacked.Infrastructure.Reporting.Filtering`):
  - `const int MaxAmount = 3650`
  - `IReadOnlyList<string> Units` = `hours, days, weeks, months`
  - `IReadOnlyList<string> Periods` = the ten period keys in reading order
  - `DateTime Subtract(DateTime nowUtc, int amount, string unit)` — UTC kind; throws `ArgumentOutOfRangeException` for an unknown unit (callers validate first)
  - `bool TryResolveZone(string? id, out TimeZoneInfo zone)` — null/empty → `true` + UTC; not IANA-shaped or not on this host → `false`
  - `(DateTime StartUtc, DateTime EndUtc) Period(DateTime nowUtc, string period, TimeZoneInfo zone)` — half-open, both UTC kind; throws for an unknown period (callers validate first)

- [ ] **Step 1: Write the failing tests**

Create `tests/JiraUnpacked.Tests/Reporting/RelativeDatesTests.cs`:

```csharp
using System.Globalization;
using JiraUnpacked.Infrastructure.Reporting.Filtering;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// The date arithmetic behind the relative operators, with "now" and the zone as arguments.
/// Every expected instant here was worked out from a calendar, not read back from the code.
/// </summary>
public sealed class RelativeDatesTests
{
    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0) =>
        new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static DateTime Day(string yyyyMmDd) =>
        DateTime.ParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static TimeZoneInfo Zone(string id)
    {
        Assert.True(RelativeDates.TryResolveZone(id, out var zone), $"'{id}' should resolve on this host.");
        return zone;
    }

    [Theory]
    [InlineData("hours", 5, 2026, 3, 31, 7)]
    [InlineData("days", 14, 2026, 3, 17, 12)]
    [InlineData("weeks", 2, 2026, 3, 17, 12)]
    [InlineData("months", 1, 2026, 2, 28, 12)]   // calendar months: March 31 minus one month is February 28
    public void Subtract_counts_back_from_now_in_each_unit(string unit, int amount, int y, int m, int d, int h)
    {
        var since = RelativeDates.Subtract(U(2026, 3, 31, 12), amount, unit);

        Assert.Equal(U(y, m, d, h), since);
        Assert.Equal(DateTimeKind.Utc, since.Kind);   // Npgsql refuses a non-UTC DateTime for timestamptz
    }

    [Theory]
    [InlineData("today",       "2026-09-23", "2026-09-24")]
    [InlineData("yesterday",   "2026-09-22", "2026-09-23")]
    [InlineData("thisWeek",    "2026-09-21", "2026-09-28")]
    [InlineData("lastWeek",    "2026-09-14", "2026-09-21")]
    [InlineData("thisMonth",   "2026-09-01", "2026-10-01")]
    [InlineData("lastMonth",   "2026-08-01", "2026-09-01")]
    [InlineData("thisQuarter", "2026-07-01", "2026-10-01")]
    [InlineData("lastQuarter", "2026-04-01", "2026-07-01")]
    [InlineData("thisYear",    "2026-01-01", "2027-01-01")]
    [InlineData("lastYear",    "2025-01-01", "2026-01-01")]
    public void Every_period_is_a_half_open_range_of_whole_days(string period, string start, string end)
    {
        var (s, e) = RelativeDates.Period(U(2026, 9, 23, 12), period, TimeZoneInfo.Utc);   // a Wednesday

        Assert.Equal(Day(start), s);
        Assert.Equal(Day(end), e);
        Assert.Equal(DateTimeKind.Utc, s.Kind);
        Assert.Equal(DateTimeKind.Utc, e.Kind);
    }

    [Theory]
    [InlineData(27, "2026-09-21")]   // Sunday: still the week that began on Monday the 21st
    [InlineData(28, "2026-09-28")]   // Monday: a new week
    public void Weeks_start_on_monday(int dayOfSeptember, string monday)
    {
        var (s, e) = RelativeDates.Period(U(2026, 9, dayOfSeptember, 12), "thisWeek", TimeZoneInfo.Utc);

        Assert.Equal(Day(monday), s);
        Assert.Equal(Day(monday).AddDays(7), e);
    }

    [Fact]
    public void Quarters_turn_over_at_midnight_on_the_first_of_the_month()
    {
        Assert.Equal((Day("2026-07-01"), Day("2026-10-01")),
            RelativeDates.Period(U(2026, 9, 30, 23, 59), "thisQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-10-01"), Day("2027-01-01")),
            RelativeDates.Period(U(2026, 10, 1), "thisQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-07-01"), Day("2026-10-01")),
            RelativeDates.Period(U(2026, 10, 1), "lastQuarter", TimeZoneInfo.Utc));
    }

    [Fact]
    public void The_last_periods_reach_back_across_the_year_boundary()
    {
        var newYear = U(2027, 1, 1, 0, 30);   // a Friday

        Assert.Equal((Day("2026-12-31"), Day("2027-01-01")), RelativeDates.Period(newYear, "yesterday", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-28"), Day("2027-01-04")), RelativeDates.Period(newYear, "thisWeek", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-21"), Day("2026-12-28")), RelativeDates.Period(newYear, "lastWeek", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-12-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastMonth", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-10-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastQuarter", TimeZoneInfo.Utc));
        Assert.Equal((Day("2026-01-01"), Day("2027-01-01")), RelativeDates.Period(newYear, "lastYear", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Today_is_the_zones_day_not_utcs()
    {
        // 23:30 UTC on the 23rd is already 01:30 on the 24th in Warsaw (CEST, UTC+2).
        var (s, e) = RelativeDates.Period(U(2026, 9, 23, 23, 30), "today", Zone("Europe/Warsaw"));

        Assert.Equal(U(2026, 9, 23, 22), s);
        Assert.Equal(U(2026, 9, 24, 22), e);
    }

    [Fact]
    public void A_period_that_spans_a_dst_change_uses_each_ends_own_offset()
    {
        // October in Warsaw starts in summer time (UTC+2) and ends in winter time (UTC+1).
        var (s, e) = RelativeDates.Period(U(2026, 10, 15, 12), "thisMonth", Zone("Europe/Warsaw"));

        Assert.Equal(U(2026, 9, 30, 22), s);
        Assert.Equal(U(2026, 10, 31, 23), e);
    }

    [Fact]
    public void A_midnight_skipped_by_dst_starts_the_day_at_the_first_time_that_exists()
    {
        // Some zones spring forward AT midnight, so 00:00 never happens that day, and converting
        // it throws. Built here rather than taken from the host's zone data, which differs between
        // Windows and Linux and changes whenever a government does.
        var zone = TimeZoneInfo.CreateCustomTimeZone(
            "Test/MidnightDst", TimeSpan.FromHours(-4), "Midnight DST", "Midnight Standard", "Midnight Daylight",
            new[]
            {
                TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                    new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 29),
                    TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 25)),
            });

        var (s, e) = RelativeDates.Period(U(2026, 3, 29, 12), "today", zone);

        Assert.Equal(U(2026, 3, 29, 4), s);   // 01:00 local, now UTC-3
        Assert.Equal(U(2026, 3, 30, 3), e);   // the next midnight, UTC-3
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_zone_is_utc(string? id)
    {
        Assert.True(RelativeDates.TryResolveZone(id, out var zone));
        Assert.Equal(TimeZoneInfo.Utc, zone);
    }

    [Theory]
    [InlineData("Europe/Warsaw")]
    [InlineData("UTC")]
    [InlineData("Etc/GMT+5")]
    [InlineData("America/Argentina/Buenos_Aires")]
    public void Iana_ids_resolve(string id) => Assert.Equal(id, Zone(id).Id);

    [Theory]
    [InlineData("Nope/Nowhere")]                     // well-formed, but no such zone
    [InlineData("../../etc/passwd")]                 // a path: never handed to the OS
    [InlineData("Central European Standard Time")]   // a Windows id: resolves on Windows only
    [InlineData("Europe/Warsaw\n")]                  // a trailing newline is not part of an id
    public void Anything_else_is_refused_on_every_host(string id) =>
        Assert.False(RelativeDates.TryResolveZone(id, out _));

    [Fact]
    public void An_id_longer_than_any_real_one_is_refused() =>
        Assert.False(RelativeDates.TryResolveZone(new string('A', 65), out _));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~RelativeDatesTests" -v q`
Expected: build FAILS — `The name 'RelativeDates' does not exist in the current context`.

- [ ] **Step 3: Implement `RelativeDates`**

Create `src/JiraUnpacked.Infrastructure/Reporting/Filtering/RelativeDates.cs`:

```csharp
using System.Text.RegularExpressions;

namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// The date arithmetic behind InLast / NotInLast / InPeriod. Pure: "now" and the zone are
/// arguments, so every boundary is testable without a clock. The translator calls it at query
/// time, so a saved "this week" means this week whenever it is opened.
/// </summary>
internal static class RelativeDates
{
    internal const int MaxAmount = 3650;

    internal static readonly IReadOnlyList<string> Units = new[] { "hours", "days", "weeks", "months" };

    /// <summary>Reading order — the order the SPA offers them in.</summary>
    internal static readonly IReadOnlyList<string> Periods = new[]
    {
        "today", "yesterday", "thisWeek", "lastWeek", "thisMonth", "lastMonth",
        "thisQuarter", "lastQuarter", "thisYear", "lastYear",
    };

    private const int MaxZoneIdLength = 64;

    /// <summary>
    /// IANA-shaped: a letter first, then letters, digits, _ + - in up to three /-separated parts
    /// ("UTC", "Europe/Warsaw", "America/Argentina/Buenos_Aires", "Etc/GMT+5"). Checked before the
    /// id reaches the OS: on Linux FindSystemTimeZoneById reads a file named after it, and a
    /// Windows id ("Central European Standard Time") resolves on Windows only — a saved view must
    /// mean the same thing on every host.
    /// </summary>
    private static readonly Regex IanaShape =
        new(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}\z", RegexOptions.CultureInvariant);

    /// <summary>now − amount·unit. Months are calendar months (AddMonths clamps Mar 31 − 1 to Feb 28).</summary>
    internal static DateTime Subtract(DateTime nowUtc, int amount, string unit)
    {
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return unit switch
        {
            "hours" => now.AddHours(-amount),
            "days" => now.AddDays(-amount),
            "weeks" => now.AddDays(-7 * amount),
            "months" => now.AddMonths(-amount),
            _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Not a relative-date unit."),
        };
    }

    /// <summary>A missing id is UTC. False = not an id this host can resolve; the caller answers 400.</summary>
    internal static bool TryResolveZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrEmpty(id)) return true;
        if (id.Length > MaxZoneIdLength || !IanaShape.IsMatch(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    /// <summary>
    /// The calendar period containing now, in the zone, as a half-open UTC range [start, end).
    /// Weeks start on Monday (ISO 8601).
    /// </summary>
    internal static (DateTime StartUtc, DateTime EndUtc) Period(DateTime nowUtc, string period, TimeZoneInfo zone)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone).Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var month = new DateTime(today.Year, today.Month, 1);
        var quarter = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        var year = new DateTime(today.Year, 1, 1);

        var (start, end) = period switch
        {
            "today" => (today, today.AddDays(1)),
            "yesterday" => (today.AddDays(-1), today),
            "thisWeek" => (monday, monday.AddDays(7)),
            "lastWeek" => (monday.AddDays(-7), monday),
            "thisMonth" => (month, month.AddMonths(1)),
            "lastMonth" => (month.AddMonths(-1), month),
            "thisQuarter" => (quarter, quarter.AddMonths(3)),
            "lastQuarter" => (quarter.AddMonths(-3), quarter),
            "thisYear" => (year, year.AddYears(1)),
            "lastYear" => (year.AddYears(-1), year),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Not a calendar period."),
        };
        return (LocalMidnightToUtc(start, zone), LocalMidnightToUtc(end, zone));
    }

    /// <summary>
    /// A local midnight as a UTC instant. Where a DST jump skips midnight itself (some zones spring
    /// forward at 00:00), the day starts at the first local time that exists; converting the
    /// missing midnight directly would throw.
    /// </summary>
    private static DateTime LocalMidnightToUtc(DateTime local, TimeZoneInfo zone)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (var i = 0; i < 16 && zone.IsInvalidTime(t); i++) t = t.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(t, zone);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~RelativeDatesTests" -v q`
Expected: PASS (all cases; `Europe/Warsaw`, `Etc/GMT+5` and `America/Argentina/Buenos_Aires` resolve on Windows through ICU and on the Linux CI runner through its zone files).

- [ ] **Step 5: Ship zone data in the runtime image**

In `Dockerfile`, in stage 3, directly after `WORKDIR /app` and before `COPY --from=api --chown=app:app /app ./`, add:

```dockerfile
# IANA time-zone data. The Alpine runtime image ships none, so without it every "in period"
# filter saved in a zone other than UTC would be refused as an unknown time zone — in
# production only, since developer machines and CI runners carry zone data of their own.
# Installed as root, before USER app.
RUN apk add --no-cache tzdata
```

- [ ] **Step 6: Check the package provides what the code needs**

Run: `docker run --rm --user root --entrypoint sh mcr.microsoft.com/dotnet/aspnet:8.0-alpine -c "apk add --no-cache tzdata >/dev/null && test -f /usr/share/zoneinfo/Europe/Warsaw && echo zoneinfo-ok"`
Expected: `zoneinfo-ok`. (Without the `apk add`, `/usr/share/zoneinfo` does not exist in this image. The full image is built in Final verification.)

- [ ] **Step 7: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Infrastructure/Reporting/Filtering/RelativeDates.cs tests/JiraUnpacked.Tests/Reporting/RelativeDatesTests.cs Dockerfile
git commit -m "feat(filters): relative-date arithmetic and IANA time zones; tzdata in the runtime image" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Between, InLast, NotInLast and InPeriod in the translator (server)

**Files:**
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs:39`, `tests/JiraUnpacked.Tests/Reporting/IssueFilterGroupTests.cs:27` (constructor)
- Modify: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs` (`AuthedTenantAsync` parameter type)
- Create: `tests/JiraUnpacked.Tests/Reporting/DateOperatorTranslationTests.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs`

**Interfaces:**
- Consumes: `RelativeDates.MaxAmount`, `.Units`, `.Periods`, `.Subtract`, `.TryResolveZone`, `.Period` (Task 1).
- Produces: `enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains, Between, InLast, NotInLast, InPeriod }`.
- Produces: `public IssueFilterTranslator(IClock clock)` — the only constructor. DI is unchanged (`IClock` is already a singleton).
- Produces (tests): `RicherFilterTestData.WithFixedClock(JiraUnpackedApiFactory, DateTime) : WebApplicationFactory<Program>`, `RicherFilterTestData.SeedIssueAsync(SeedContext ctx, string key, DateTime? updatedAt = null, Guid? statusId = null, Guid? sprintId = null)`, `RicherFilterTestData.KeysAsync(HttpClient, Guid projectId, params object[] conditions) : Task<string[]>` (sorted ordinal), `RicherFilterTestData.RejectionAsync(HttpClient, Guid projectId, params object[] conditions) : Task<string>` (the 400's title).
- Produces: `MetricsTestData.AuthedTenantAsync(WebApplicationFactory<Program> factory)` — widened from `JiraUnpackedApiFactory`; every existing caller still compiles.

- [ ] **Step 1: Write the failing translator tests**

Create `tests/JiraUnpacked.Tests/Reporting/DateOperatorTranslationTests.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// Between / InLast / NotInLast / InPeriod through the translator, its clock pinned to a
/// Wednesday noon. Predicates are compiled and run in memory, like the other translator tests.
/// </summary>
public sealed class DateOperatorTranslationTests
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(Now));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> NoDictionary(string lookup, IReadOnlyList<string> names, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, NoDictionary, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, NoDictionary, CancellationToken.None);
        Assert.Null(r.Predicate);
        return Assert.IsType<string>(r.Error);
    }

    private static DateTime U(int y, int mo, int d, int h = 0, int mi = 0, int s = 0) =>
        new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    private static DateTime Day(string yyyyMmDd) =>
        DateTime.ParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static Issue At(DateTime at) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", JiraCreatedAt = at, JiraUpdatedAt = at,
    };

    private static Issue Pts(decimal? points) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", StoryPoints = points,
        JiraCreatedAt = Now, JiraUpdatedAt = Now,
    };

    private static FilterCondition Last(int amount, string unit, FilterOperator op = FilterOperator.InLast) =>
        new("updatedAt", op, J(new { amount, unit }), null);

    private static FilterCondition Period(string period, string? timeZone = null) =>
        new("updatedAt", FilterOperator.InPeriod,
            timeZone is null ? J(new { period }) : J(new { period, timeZone }), null);

    private static FilterCondition Between(string field, object? from, object? to) =>
        new(field, FilterOperator.Between, null, new List<JsonElement> { J(from), J(to) });

    // ---- InLast / NotInLast ----

    [Theory]
    [InlineData("hours", 5, 2026, 9, 23, 7)]
    [InlineData("days", 14, 2026, 9, 9, 12)]
    [InlineData("weeks", 2, 2026, 9, 9, 12)]
    [InlineData("months", 1, 2026, 8, 23, 12)]
    public async Task InLast_and_NotInLast_split_at_now_minus_the_amount(
        string unit, int amount, int y, int m, int d, int h)
    {
        var boundary = U(y, m, d, h);
        var inLast = await Pred(Last(amount, unit));
        var notInLast = await Pred(Last(amount, unit, FilterOperator.NotInLast));

        Assert.True(inLast(At(boundary)));                     // the boundary itself is "in the last"
        Assert.False(inLast(At(boundary.AddSeconds(-1))));
        Assert.False(notInLast(At(boundary)));
        Assert.True(notInLast(At(boundary.AddSeconds(-1))));   // "more than 14 days ago"
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3651)]
    public async Task InLast_refuses_an_amount_outside_1_to_3650(int amount) =>
        Assert.Contains("amount", await Error(Last(amount, "days")));

    [Fact]
    public async Task InLast_refuses_a_fraction_an_unknown_unit_and_a_value_of_the_wrong_shape()
    {
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.InLast, J(new { amount = 1.5, unit = "days" }), null)));
        Assert.Contains("'years'", await Error(Last(2, "years")));
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.InLast, J("14 days"), null)));
        Assert.Contains("amount", await Error(new("updatedAt", FilterOperator.NotInLast, null, null)));
    }

    // ---- InPeriod ----

    [Theory]
    [InlineData("today",       "2026-09-23", "2026-09-24")]
    [InlineData("yesterday",   "2026-09-22", "2026-09-23")]
    [InlineData("thisWeek",    "2026-09-21", "2026-09-28")]
    [InlineData("lastWeek",    "2026-09-14", "2026-09-21")]
    [InlineData("thisMonth",   "2026-09-01", "2026-10-01")]
    [InlineData("lastMonth",   "2026-08-01", "2026-09-01")]
    [InlineData("thisQuarter", "2026-07-01", "2026-10-01")]
    [InlineData("lastQuarter", "2026-04-01", "2026-07-01")]
    [InlineData("thisYear",    "2026-01-01", "2027-01-01")]
    [InlineData("lastYear",    "2025-01-01", "2026-01-01")]
    public async Task InPeriod_includes_its_first_instant_and_excludes_the_next_periods(string period, string start, string end)
    {
        var (s, e) = (Day(start), Day(end));
        var pred = await Pred(Period(period));

        Assert.True(pred(At(s)));
        Assert.True(pred(At(e.AddSeconds(-1))));
        Assert.False(pred(At(s.AddSeconds(-1))));
        Assert.False(pred(At(e)));
    }

    [Fact]
    public async Task InPeriod_uses_the_conditions_zone_and_a_missing_or_null_zone_means_utc()
    {
        // Noon UTC on the 23rd. In Warsaw (UTC+2) "today" began at 22:00 UTC on the 22nd.
        var lateOn22ndUtc = U(2026, 9, 22, 22, 30);
        var nullZone = new FilterCondition("updatedAt", FilterOperator.InPeriod,
            J(new { period = "today", timeZone = (string?)null }), null);

        Assert.True((await Pred(Period("today", "Europe/Warsaw")))(At(lateOn22ndUtc)));
        Assert.False((await Pred(Period("today")))(At(lateOn22ndUtc)));
        Assert.False((await Pred(nullZone))(At(lateOn22ndUtc)));
    }

    [Theory]
    [InlineData("Nope/Nowhere")]
    [InlineData("../../etc/passwd")]
    [InlineData("Central European Standard Time")]
    public async Task InPeriod_refuses_a_time_zone_that_is_not_an_iana_id_this_host_has(string zone) =>
        Assert.Contains("Unknown time zone", await Error(Period("today", zone)));

    [Fact]
    public async Task InPeriod_refuses_an_unknown_period_a_missing_one_and_a_zone_that_is_not_a_string()
    {
        Assert.Contains("'fortnight'", await Error(Period("fortnight")));
        Assert.Contains("period", await Error(new("updatedAt", FilterOperator.InPeriod, J(new { timeZone = "UTC" }), null)));
        Assert.Contains("timeZone", await Error(new("updatedAt", FilterOperator.InPeriod, J(new { period = "today", timeZone = 5 }), null)));
    }

    // ---- Between on dates ----

    [Fact]
    public async Task Between_dates_with_a_date_only_end_includes_that_whole_day()
    {
        var pred = await Pred(Between("updatedAt", "2026-09-01", "2026-09-30"));

        Assert.True(pred(At(U(2026, 9, 1))));
        Assert.False(pred(At(U(2026, 8, 31, 23, 59, 59))));
        Assert.True(pred(At(U(2026, 9, 30, 23, 59, 59))));
        Assert.False(pred(At(U(2026, 10, 1))));
    }

    [Fact]
    public async Task Between_dates_with_a_timestamp_end_stops_at_that_instant()
    {
        var pred = await Pred(Between("createdAt", "2026-09-01", "2026-09-30T12:00:00Z"));

        Assert.True(pred(At(U(2026, 9, 30, 12))));
        Assert.False(pred(At(U(2026, 9, 30, 12, 0, 1))));
    }

    [Fact]
    public async Task Between_dates_with_one_open_bound()
    {
        var until = await Pred(Between("updatedAt", null, "2026-09-30"));
        Assert.True(until(At(U(2001, 1, 1))));
        Assert.False(until(At(U(2026, 10, 1))));

        var since = await Pred(Between("updatedAt", "2026-09-01", null));
        Assert.True(since(At(U(2099, 1, 1))));
        Assert.False(since(At(U(2026, 8, 31, 23, 59, 59))));
    }

    [Fact]
    public async Task Between_dates_judges_from_after_to_against_the_whole_end_day()
    {
        // 10:00 on the 30th is inside "to 2026-09-30", which runs to the end of that day.
        var pred = await Pred(Between("updatedAt", "2026-09-30T10:00:00Z", "2026-09-30"));
        Assert.True(pred(At(U(2026, 9, 30, 11))));
    }

    [Fact]
    public async Task Between_dates_refuses_no_bounds_reversed_bounds_and_malformed_values()
    {
        Assert.Contains("at least one bound", await Error(Between("updatedAt", null, null)));
        Assert.Contains("'from' is after 'to'", await Error(Between("updatedAt", "2026-09-30", "2026-09-01")));
        Assert.Contains("[from, to]", await Error(new("updatedAt", FilterOperator.Between, null, new List<JsonElement> { J("2026-09-01") })));
        Assert.Contains("ISO-8601", await Error(Between("updatedAt", "last tuesday", null)));
    }

    // ---- Between on story points ----

    [Fact]
    public async Task Between_story_points_is_inclusive_at_both_ends_and_takes_decimals()
    {
        var pred = await Pred(Between("storyPoints", 1.5m, 3));

        Assert.True(pred(Pts(1.5m)));
        Assert.True(pred(Pts(3m)));
        Assert.False(pred(Pts(1.4m)));
        Assert.False(pred(Pts(3.01m)));
        Assert.False(pred(Pts(null)));
    }

    [Fact]
    public async Task Between_story_points_with_an_open_bound_still_leaves_out_unestimated_issues()
    {
        var pred = await Pred(Between("storyPoints", null, 3));

        Assert.True(pred(Pts(0m)));
        Assert.False(pred(Pts(null)));
    }

    [Fact]
    public async Task Between_story_points_refuses_reversed_bounds_and_non_numbers()
    {
        Assert.Contains("'from' is greater than 'to'", await Error(Between("storyPoints", 5, 3)));
        Assert.Contains("numbers", await Error(Between("storyPoints", "1", 3)));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("summary")]
    public async Task Between_is_refused_on_fields_that_do_not_offer_it(string field) =>
        Assert.Equal($"Operator 'Between' is not allowed for field '{field}'.", await Error(Between(field, 1, 2)));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~DateOperatorTranslationTests" -v q`
Expected: build FAILS — `'FilterOperator' does not contain a definition for 'InLast'` and `'IssueFilterTranslator' does not contain a constructor that takes 1 arguments`.

- [ ] **Step 3: Append the operators**

In `src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs`, replace the enum line:

```csharp
public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull, NotIn, IsNotNull, NotContains, Between, InLast, NotInLast, InPeriod }
```

and replace the `FilterCondition` doc comment with:

```csharp
/// <summary>
/// One filter condition. Value for single-value ops; Values for In/NotIn, where a JSON null
/// element stands for (Empty), and for Between as [from, to] with null = an open bound. Both null
/// for IsNull/IsNotNull. Value is an object for InLast/NotInLast ({"amount", "unit"}) and InPeriod
/// ({"period", "timeZone"}). Disabled = kept in the definition but skipped entirely: not
/// validated, not counted, not applied.
/// </summary>
```

- [ ] **Step 4: Offer them on the right fields, in reading order**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`, replace the `storyPoints` entry's operator list and the `Date` helper:

```csharp
            ["storyPoints"]= new("storyPoints", FieldType.Decimal,
                                 Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.Lt, FilterOperator.Lte,
                                     FilterOperator.Gt, FilterOperator.Gte, FilterOperator.Between,
                                     FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, decimal?>>)(i => i.StoryPoints)),
```

```csharp
    private static FilterField Date(string name, Expression<Func<Issue, DateTime>> sel) =>
        new(name, FieldType.DateTime,
            Ops(FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte,
                FilterOperator.Between, FilterOperator.InLast, FilterOperator.NotInLast, FilterOperator.InPeriod), sel);
```

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterCatalogService.cs`, replace `DisplayOrder` (and the first sentence of its comment):

```csharp
    /// <summary>
    /// The order the editor offers operators in: equality, lists, comparisons, ranges and
    /// relative dates, text matching, then the two that take no value. Explicit because
    /// FilterOperator is append-only, which makes its declaration order a history rather than a
    /// reading order. Every member must appear here — FilterCatalogServiceTests checks.
    /// </summary>
    internal static readonly FilterOperator[] DisplayOrder =
    {
        FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
        FilterOperator.Lt, FilterOperator.Lte, FilterOperator.Gt, FilterOperator.Gte,
        FilterOperator.Between, FilterOperator.InLast, FilterOperator.NotInLast, FilterOperator.InPeriod,
        FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.StartsWith,
        FilterOperator.IsNull, FilterOperator.IsNotNull,
    };
```

- [ ] **Step 5: Give the translator a clock and the four operators**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`:

(a) Replace the using block at the top with:

```csharp
using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using Microsoft.EntityFrameworkCore;
```

(b) Directly after `public const int MaxDepth = 3;` add:

```csharp

    private readonly IClock _clock;

    /// <summary>
    /// The clock is what makes a relative condition relative: "in the last 14 days" is resolved
    /// against it on every build, so a saved view means the same thing whenever it is opened.
    /// </summary>
    public IssueFilterTranslator(IClock clock) => _clock = clock;
```

(c) `BuildConditionAsync` becomes an instance method (it dispatches to the clock-using `BuildDate`). Replace its signature line:

```csharp
    private async Task<(Expression<Func<Issue, bool>>?, string?)> BuildConditionAsync(
```

(d) In `BuildDecimal`, directly after the line `if (c.Op == FilterOperator.IsNotNull) return (Lambda(notNull, p), null);` add:

```csharp
        if (c.Op == FilterOperator.Between) return BuildDecimalBetween(c, body, p);
```

(e) Replace the whole `BuildDate` method with:

```csharp
    private (Expression<Func<Issue, bool>>?, string?) BuildDate(FilterField field, FilterCondition c)
    {
        var sel = (Expression<Func<Issue, DateTime>>)field.Selector;
        var body = sel.Body; var p = sel.Parameters[0];

        switch (c.Op)
        {
            case FilterOperator.Between: return BuildDateBetween(c, body, p);
            case FilterOperator.InLast or FilterOperator.NotInLast: return BuildDateInLast(c, body, p);
            case FilterOperator.InPeriod: return BuildDateInPeriod(c, body, p);
        }

        if (c.Value is not { ValueKind: JsonValueKind.String } sv || !DateTime.TryParse(
                sv.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return (null, $"Field '{c.Field}' requires an ISO-8601 date 'value'.");
        var k = Expression.Constant(dt, typeof(DateTime));
        Expression cmp = c.Op switch
        {
            FilterOperator.Lt => Expression.LessThan(body, k),
            FilterOperator.Lte => Expression.LessThanOrEqual(body, k),
            FilterOperator.Gt => Expression.GreaterThan(body, k),
            FilterOperator.Gte => Expression.GreaterThanOrEqual(body, k),
            _ => Expression.Constant(false),
        };
        return (Lambda(cmp, p), null);
    }

    // ---- ranges and relative dates ----

    /// <summary>`values: [from, to]` — exactly two; either may be JSON null (open), not both.</summary>
    private static string? ReadBounds(FilterCondition c, out JsonElement? from, out JsonElement? to)
    {
        from = null; to = null;
        if (c.Values is not { Count: 2 })
            return $"'Between' on '{c.Field}' requires 'values': [from, to].";
        if (c.Values[0].ValueKind != JsonValueKind.Null) from = c.Values[0];
        if (c.Values[1].ValueKind != JsonValueKind.Null) to = c.Values[1];
        return from is null && to is null
            ? $"'Between' on '{c.Field}' needs at least one bound; both are empty."
            : null;
    }

    /// <summary>Joins an optional left part with a right part.</summary>
    private static Expression And(Expression? a, Expression b) => a is null ? b : Expression.AndAlso(a, b);

    /// <summary>
    /// `from ≤ x ≤ to`, either bound open. An unestimated issue never matches: the lifted
    /// comparison is false for NULL, in memory and in SQL alike.
    /// </summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDecimalBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        var notNumbers = $"'Between' on '{c.Field}' requires numbers (or null) in 'values'.";

        decimal? from = null, to = null;
        if (fromEl is { } f)
        {
            if (f.ValueKind != JsonValueKind.Number || !f.TryGetDecimal(out var d)) return (null, notNumbers);
            from = d;
        }
        if (toEl is { } t)
        {
            if (t.ValueKind != JsonValueKind.Number || !t.TryGetDecimal(out var d)) return (null, notNumbers);
            to = d;
        }
        if (from > to) return (null, $"'Between' on '{c.Field}': 'from' is greater than 'to'.");

        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, Expression.Constant((decimal?)lo, typeof(decimal?)));
        if (to is { } hi) cmp = And(cmp, Expression.LessThanOrEqual(body, Expression.Constant((decimal?)hi, typeof(decimal?))));
        return (Lambda(cmp!, p), null);
    }

    private static readonly Regex DateOnlyShape = new(@"^\d{4}-\d{2}-\d{2}\z", RegexOptions.CultureInvariant);

    /// <summary>An ISO-8601 date or timestamp, read as UTC like every other date here.</summary>
    private static bool TryReadDate(JsonElement v, out DateTime value, out bool dateOnly)
    {
        value = default; dateOnly = false;
        if (v.ValueKind != JsonValueKind.String) return false;
        var s = v.GetString()!;
        dateOnly = DateOnlyShape.IsMatch(s);
        return DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>
    /// `from ≤ x ≤ to`. A date-only `to` means "through that day": everything before the next
    /// midnight (UTC). A full timestamp is used as-is.
    /// </summary>
    private static (Expression<Func<Issue, bool>>?, string?) BuildDateBetween(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (ReadBounds(c, out var fromEl, out var toEl) is { } boundsError) return (null, boundsError);
        var notDates = $"'Between' on '{c.Field}' requires ISO-8601 dates (or null) in 'values'.";

        DateTime? from = null, to = null;
        var toDateOnly = false;
        if (fromEl is { } f)
        {
            if (!TryReadDate(f, out var d, out _)) return (null, notDates);
            from = d;
        }
        if (toEl is { } t)
        {
            if (!TryReadDate(t, out var d, out toDateOnly)) return (null, notDates);
            to = d;
        }
        // Judged against the effective end, so "from 10:00 on the 30th to the 30th" is not reversed.
        if (from is { } a && to is { } b && (toDateOnly ? a >= b.AddDays(1) : a > b))
            return (null, $"'Between' on '{c.Field}': 'from' is after 'to'.");

        Expression? cmp = null;
        if (from is { } lo) cmp = Expression.GreaterThanOrEqual(body, Expression.Constant(lo));
        if (to is { } hi)
            cmp = And(cmp, toDateOnly
                ? Expression.LessThan(body, Expression.Constant(hi.AddDays(1)))
                : Expression.LessThanOrEqual(body, Expression.Constant(hi)));
        return (Lambda(cmp!, p), null);
    }

    /// <summary>InLast: `x ≥ now − amount·unit`. NotInLast: `x < now − amount·unit` ("more than … ago").</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInLast(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number
            || !v.TryGetProperty("unit", out var u) || u.ValueKind != JsonValueKind.String)
            return (null, $"'{c.Op}' on '{c.Field}' requires 'value': {{\"amount\": 14, \"unit\": \"days\"}}.");
        if (!a.TryGetInt32(out var amount) || amount < 1 || amount > RelativeDates.MaxAmount)
            return (null, $"'amount' must be a whole number from 1 to {RelativeDates.MaxAmount}.");
        var unit = u.GetString()!;
        if (!RelativeDates.Units.Contains(unit))
            return (null, $"Unknown unit '{unit}'. Use one of: {string.Join(", ", RelativeDates.Units)}.");

        var since = Expression.Constant(RelativeDates.Subtract(_clock.UtcNow, amount, unit));
        return (Lambda(c.Op == FilterOperator.InLast
            ? Expression.GreaterThanOrEqual(body, since)
            : Expression.LessThan(body, since), p), null);
    }

    /// <summary>`start ≤ x < end` of the calendar period containing now, in the condition's zone (missing = UTC).</summary>
    private (Expression<Func<Issue, bool>>?, string?) BuildDateInPeriod(
        FilterCondition c, Expression body, ParameterExpression p)
    {
        if (c.Value is not { ValueKind: JsonValueKind.Object } v
            || !v.TryGetProperty("period", out var pe) || pe.ValueKind != JsonValueKind.String)
            return (null, $"'InPeriod' on '{c.Field}' requires 'value': {{\"period\": \"thisWeek\", \"timeZone\": \"Europe/Warsaw\"}}.");
        var period = pe.GetString()!;
        if (!RelativeDates.Periods.Contains(period))
            return (null, $"Unknown period '{period}'. Use one of: {string.Join(", ", RelativeDates.Periods)}.");

        string? zoneId = null;
        if (v.TryGetProperty("timeZone", out var tz) && tz.ValueKind != JsonValueKind.Null)
        {
            if (tz.ValueKind != JsonValueKind.String)
                return (null, "'timeZone' must be an IANA time zone id, such as \"Europe/Warsaw\".");
            zoneId = tz.GetString();
        }
        if (!RelativeDates.TryResolveZone(zoneId, out var zone))
            return (null, $"Unknown time zone '{zoneId}'. Use an IANA id, such as \"Europe/Warsaw\".");

        var (start, end) = RelativeDates.Period(_clock.UtcNow, period, zone);
        return (Lambda(Expression.AndAlso(
            Expression.GreaterThanOrEqual(body, Expression.Constant(start)),
            Expression.LessThan(body, Expression.Constant(end))), p), null);
    }
```

- [ ] **Step 6: Construct the translator with a clock in the existing tests**

In `tests/JiraUnpacked.Tests/Reporting/IssueFilterTranslatorTests.cs` and `tests/JiraUnpacked.Tests/Reporting/IssueFilterGroupTests.cs`, add `using JiraUnpacked.Tests.Security;` to the usings and replace

```csharp
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator();
```

with

```csharp
    // None of these conditions is relative, so the time is irrelevant — but there is always a clock.
    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));
```

- [ ] **Step 7: Pin the catalog's new operator sets**

In `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`, in `Operators_are_listed_in_reading_order_not_declaration_order`, replace the `storyPoints` and `createdAt` lines with:

```csharp
        Assert.Equal(new[] { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "Between", "IsNull", "IsNotNull" }, OpsOf("storyPoints"));
        Assert.Equal(new[] { "Lt", "Lte", "Gt", "Gte", "Between", "InLast", "NotInLast", "InPeriod" }, OpsOf("createdAt"));
        Assert.Equal(new[] { "Lt", "Lte", "Gt", "Gte", "Between", "InLast", "NotInLast", "InPeriod" }, OpsOf("updatedAt"));
```

In `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`, replace the `storyPoints` assertion and add the two date fields after it:

```csharp
        var storyPoints = catalog.Fields.Single(f => f.Name == "storyPoints");
        Assert.True(new HashSet<string> { "Eq", "Neq", "Lt", "Lte", "Gt", "Gte", "Between", "IsNull", "IsNotNull" }.SetEquals(storyPoints.Operators));

        foreach (var date in new[] { "createdAt", "updatedAt" })
        {
            var field = catalog.Fields.Single(f => f.Name == date);
            Assert.True(new HashSet<string> { "Lt", "Lte", "Gt", "Gte", "Between", "InLast", "NotInLast", "InPeriod" }.SetEquals(field.Operators));
        }
```

- [ ] **Step 8: Run the unit tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~DateOperatorTranslationTests|FullyQualifiedName~IssueFilterTranslatorTests|FullyQualifiedName~IssueFilterGroupTests|FullyQualifiedName~FilterCatalogServiceTests|FullyQualifiedName~RelativeDatesTests" -v q`
Expected: PASS.

- [ ] **Step 9: Write the failing end-to-end tests (fixed clock in the harness)**

In `tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs`, add `using Microsoft.AspNetCore.Mvc.Testing;` and change the signature of `AuthedTenantAsync` to take the base factory type (the body is unchanged — it only uses `Services` and `CreateClient()`):

```csharp
    internal static async Task<(HttpClient client, Guid tenantId)> AuthedTenantAsync(WebApplicationFactory<Program> factory)
```

Create `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Seeding and query helpers for the richer-filter-value tests. Seeding uses the superuser
/// connection (bypasses RLS) purely to build fixtures; every assertion reads through the API as
/// the owning tenant, and every call works in a fresh project so parallel classes never collide.
/// </summary>
internal static class RicherFilterTestData
{
    /// <summary>
    /// The API with the FILTER TRANSLATOR's clock pinned — only the translator's: the token
    /// service keeps the real clock, or every token this host issued would already have expired.
    /// Registered after the app's own registration, so it is the one resolved. The caller must
    /// have migrated the database first (the app seeds an admin while the host is built).
    /// </summary>
    internal static WebApplicationFactory<Program> WithFixedClock(JiraUnpackedApiFactory factory, DateTime nowUtc) =>
        factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddScoped<IIssueFilterTranslator>(_ => new IssueFilterTranslator(new FakeClock(nowUtc)))));

    private static NpgsqlConnection Conn() => new(JiraUnpackedApiFactory.TestConnectionString);

    /// <summary>One issue; created = updated = <paramref name="updatedAt"/> (default Jan 1 2026).</summary>
    internal static async Task SeedIssueAsync(
        SeedContext ctx, string key, DateTime? updatedAt = null, Guid? statusId = null, Guid? sprintId = null)
    {
        var at = updatedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Issues""(""Id"",""EndUserId"",""JiraProjectId"",""JiraIssueId"",""JiraKey"",""Summary"",""StatusId"",""SprintId"",""JiraCreatedAt"",""JiraUpdatedAt"",""LastSyncedAt"")
              VALUES (@i,@t,@p,@jid,@k,@k,@st,@sp,@at,@at,now())",
            ("i", Guid.NewGuid()), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("jid", key), ("k", key),
            ("st", statusId), ("sp", sprintId), ("at", at));
    }

    private static Task<HttpResponseMessage> QueryAsync(HttpClient client, Guid projectId, object[] conditions) =>
        client.PostAsJsonAsync($"/api/projects/{projectId}/issues/query",
            new { filter = new { logic = "And", conditions } });

    /// <summary>The matching keys, sorted ordinally, from one And group of the given conditions.</summary>
    internal static async Task<string[]> KeysAsync(HttpClient client, Guid projectId, params object[] conditions)
    {
        var resp = await QueryAsync(client, projectId, conditions);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var page = await resp.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>();
        return page!.Items.Select(i => i.JiraKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Asserts a 400 and returns its ProblemDetails title.</summary>
    internal static async Task<string> RejectionAsync(HttpClient client, Guid projectId, params object[] conditions)
    {
        var resp = await QueryAsync(client, projectId, conditions);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;
    }
}
```

Create `tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// Relative dates end to end: query and export resolve them on the server, against the server's
/// clock — pinned here to a Wednesday noon — so every expectation is exact.
/// </summary>
public sealed class RelativeDateQueryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly JiraUnpackedApiFactory _factory;
    private WebApplicationFactory<Program> _app = null!;

    public RelativeDateQueryTests(JiraUnpackedApiFactory f) => _factory = f;

    public async Task InitializeAsync()
    {
        await _factory.EnsureDatabaseAsync();   // migrate BEFORE any host is built
        _app = RicherFilterTestData.WithFixedClock(_factory, Now);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static object InLast(string op, int amount, string unit) =>
        new { field = "updatedAt", op, value = new { amount, unit } };

    private static object InPeriod(string period, string? timeZone = null) =>
        timeZone is null
            ? new { field = "updatedAt", op = "InPeriod", value = (object)new { period } }
            : new { field = "updatedAt", op = "InPeriod", value = (object)new { period, timeZone } };

    private static object Between(object? from, object? to) =>
        new { field = "updatedAt", op = "Between", values = new[] { from, to } };

    /// <summary>R-1 yesterday, R-2 on Aug 20 (noon UTC), R-3 in 2025.</summary>
    private async Task<(HttpClient client, SeedContext ctx)> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_app);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-1", Now.AddDays(-1));
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-2", Now.AddDays(-34));
        await RicherFilterTestData.SeedIssueAsync(ctx, "R-3", Now.AddDays(-400));
        return (client, ctx);
    }

    [Fact]
    public async Task Relative_conditions_are_resolved_against_the_servers_clock()
    {
        var (client, ctx) = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);

        Assert.Equal(new[] { "R-1" }, await Keys(InLast("InLast", 14, "days")));
        Assert.Equal(new[] { "R-2", "R-3" }, await Keys(InLast("NotInLast", 14, "days")));
        Assert.Equal(new[] { "R-1" }, await Keys(InPeriod("thisMonth")));
        Assert.Equal(new[] { "R-2" }, await Keys(InPeriod("lastMonth", "Europe/Warsaw")));
        // A date-only end includes the whole of Aug 20, where R-2 sits at noon.
        Assert.Equal(new[] { "R-2" }, await Keys(Between("2026-08-01", "2026-08-20")));
        Assert.Equal(new[] { "R-2", "R-3" }, await Keys(Between(null, "2026-08-20")));
    }

    [Fact]
    public async Task A_bad_relative_value_is_a_400_naming_the_problem()
    {
        var (client, ctx) = await SeedAsync();
        Task<string> Title(object c) => RicherFilterTestData.RejectionAsync(client, ctx.ProjectId, c);

        Assert.Contains("Nope/Nowhere", await Title(InPeriod("today", "Nope/Nowhere")));
        Assert.Contains("Unknown time zone", await Title(InPeriod("today", "../../etc/passwd")));
        Assert.Contains("amount", await Title(InLast("InLast", 0, "days")));
        Assert.Contains("at least one bound", await Title(Between(null, null)));
        Assert.Contains("'from' is after 'to'", await Title(Between("2026-09-30", "2026-09-01")));
    }

    [Fact]
    public async Task Export_resolves_relative_conditions_with_the_same_clock()
    {
        var (client, ctx) = await SeedAsync();
        var filter = JsonSerializer.Serialize(new
        {
            logic = "And",
            conditions = new[] { new { field = "updatedAt", op = "InLast", value = new { amount = 14, unit = "days" } } },
        });

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey&filter={Uri.EscapeDataString(filter)}");

        Assert.Equal(new[] { "Key", "R-1" }, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }
}
```

- [ ] **Step 10: Run the integration tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~RelativeDateQueryTests|FullyQualifiedName~FilterCatalogEndpointTests|FullyQualifiedName~IssueFilterTests|FullyQualifiedName~IssueExportTests|FullyQualifiedName~FilterValuesTests" -v q`
Expected: PASS. (A `Cannot write DateTime with Kind=Unspecified` error here would mean a relative constant was built without UTC kind — every path in Task 1/2 returns UTC.)

- [ ] **Step 11: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IssueFilter.cs src/JiraUnpacked.Infrastructure/Reporting/Filtering tests/JiraUnpacked.Tests/Reporting tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs tests/JiraUnpacked.Tests/Integration/MetricsTestData.cs tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs tests/JiraUnpacked.Tests/Integration/RelativeDateQueryTests.cs
git commit -m "feat(filters): Between, InLast, NotInLast and InPeriod, resolved by the server's clock" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Sprint smart values (server)

**Files:**
- Create: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SmartSprints.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` (`DictNameResolver` doc, `FilterLookups`, `BuildDictAsync`)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs` (`ResolveDictAsync`)
- Modify: `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`
- Modify: `src/JiraUnpacked.Api/Controllers/IssuesController.cs` (`FilterValues`)
- Modify: `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs` (+ `SeedSprintAsync`)
- Create: `tests/JiraUnpacked.Tests/Reporting/SmartSprintFilterTests.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs`

**Interfaces:**
- Produces: `internal static class SmartSprints { internal sealed record Smart(string Key, string Label, IReadOnlyList<string> States); internal static readonly IReadOnlyList<Smart> All; internal static Smart? Find(string key); }` — `All` in the order `active, future, open, closed`.
- Produces: `public static class FilterLookups { public const string SprintState = "sprint.state"; }` (Task 4 adds two more constants). A resolver called with `FilterLookups.SprintState` and a list of states returns the ids of sprints in those states.
- Produces: `public sealed record SmartValueCount(string Key, string Label, int Count);` and `FilterValuesResult(IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error, IReadOnlyList<SmartValueCount>? Smart = null)`.
- Produces (HTTP): `GET /api/projects/{id}/filter-values?field=sprint` → `{ field, values, emptyCount, smart: [{ key, label, count }] }`; every other field → no `smart` property.
- Produces (tests): `RicherFilterTestData.SeedSprintAsync(SeedContext ctx, string name, string state) : Task<Guid>`.

- [ ] **Step 1: Write the failing translator tests**

Create `tests/JiraUnpacked.Tests/Reporting/SmartSprintFilterTests.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// Smart sprint values: a JSON object resolved from Sprint.State through the resolver, never a
/// string, so a sprint NAMED "active" is still reachable by name.
/// </summary>
public sealed class SmartSprintFilterTests
{
    private static readonly Guid ActiveId = Guid.NewGuid();
    private static readonly Guid FutureId = Guid.NewGuid();
    private static readonly Guid ClosedId = Guid.NewGuid();
    private static readonly Guid NamedActiveId = Guid.NewGuid();   // a CLOSED sprint someone named "active"

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);
    private static JsonElement Smart(string key) => J(new { smart = key });

    /// <summary>The compiler's resolver in miniature: names by name, smart values by state.</summary>
    private static DictNameResolver Sprints(bool withActive = true) => (lookup, names, ct) =>
    {
        var byState = new Dictionary<string, Guid[]>
        {
            ["active"] = withActive ? new[] { ActiveId } : Array.Empty<Guid>(),
            ["future"] = new[] { FutureId },
            ["closed"] = new[] { ClosedId, NamedActiveId },
        };
        var byName = new Dictionary<string, Guid> { ["active"] = NamedActiveId, ["Sprint 7"] = ClosedId };
        IReadOnlyList<Guid> ids = lookup switch
        {
            FilterLookups.SprintState => names.SelectMany(s => byState.TryGetValue(s, out var g) ? g : Array.Empty<Guid>()).ToList(),
            "sprint" => names.Where(byName.ContainsKey).Select(n => byName[n]).ToList(),
            _ => Array.Empty<Guid>(),
        };
        return Task.FromResult(ids);
    };

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c, bool withActive = true)
    {
        var r = await Sut.BuildAsync(new[] { c }, Sprints(withActive), CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static async Task<string> Error(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, Sprints(), CancellationToken.None);
        return Assert.IsType<string>(r.Error);
    }

    private static Issue In(Guid? sprintId) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", SprintId = sprintId,
        JiraCreatedAt = DateTime.UtcNow, JiraUpdatedAt = DateTime.UtcNow,
    };

    private static FilterCondition Values(FilterOperator op, params JsonElement[] values) =>
        new("sprint", op, null, values.ToList());

    [Fact]
    public async Task Each_smart_key_means_its_states()
    {
        var active = await Pred(Values(FilterOperator.In, Smart("active")));
        Assert.True(active(In(ActiveId)));
        Assert.False(active(In(FutureId)));
        Assert.False(active(In(NamedActiveId)));
        Assert.False(active(In(null)));

        var open = await Pred(Values(FilterOperator.In, Smart("open")));
        Assert.True(open(In(ActiveId)));
        Assert.True(open(In(FutureId)));
        Assert.False(open(In(ClosedId)));

        var closed = await Pred(Values(FilterOperator.In, Smart("closed")));
        Assert.True(closed(In(ClosedId)));
        Assert.True(closed(In(NamedActiveId)));
        Assert.False(closed(In(ActiveId)));

        var future = await Pred(new FilterCondition("sprint", FilterOperator.Eq, Smart("future"), null));
        Assert.True(future(In(FutureId)));
        Assert.False(future(In(ActiveId)));
    }

    [Fact]
    public async Task Negated_smart_values_follow_the_empty_rule()
    {
        var notActive = await Pred(new FilterCondition("sprint", FilterOperator.Neq, Smart("active"), null));
        Assert.True(notActive(In(FutureId)));
        Assert.False(notActive(In(ActiveId)));
        Assert.False(notActive(In(null)));   // "is not the active sprint" says nothing about no sprint

        var notOpen = await Pred(Values(FilterOperator.NotIn, Smart("open")));
        Assert.True(notOpen(In(ClosedId)));
        Assert.False(notOpen(In(FutureId)));
        Assert.False(notOpen(In(null)));
    }

    [Fact]
    public async Task Smart_values_names_and_empty_mix_in_one_list()
    {
        var pred = await Pred(Values(FilterOperator.In, Smart("active"), J("Sprint 7"), J(null)));

        Assert.True(pred(In(ActiveId)));
        Assert.True(pred(In(ClosedId)));     // "Sprint 7", by name
        Assert.True(pred(In(null)));         // (Empty)
        Assert.False(pred(In(FutureId)));
    }

    [Fact]
    public async Task A_string_active_is_a_sprint_name_not_a_state()
    {
        var pred = await Pred(Values(FilterOperator.In, J("active")));

        Assert.True(pred(In(NamedActiveId)));
        Assert.False(pred(In(ActiveId)));
    }

    [Fact]
    public async Task No_active_sprint_matches_nothing_rather_than_failing()
    {
        var pred = await Pred(Values(FilterOperator.In, Smart("active")), withActive: false);

        Assert.False(pred(In(FutureId)));
        Assert.False(pred(In(ClosedId)));
        Assert.False(pred(In(null)));
    }

    [Fact]
    public async Task Smart_values_are_refused_off_sprint_unknown_or_malformed()
    {
        Assert.Contains("only available on 'sprint'",
            await Error(new FilterCondition("status", FilterOperator.In, null, new List<JsonElement> { Smart("active") })));
        Assert.Contains("only available on 'sprint'",
            await Error(new FilterCondition("team", FilterOperator.Eq, Smart("active"), null)));
        Assert.Contains("'current'", await Error(Values(FilterOperator.In, Smart("current"))));
        Assert.Contains("{\"smart\"", await Error(Values(FilterOperator.In, J(new { name = "Sprint 7" }))));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SmartSprintFilterTests" -v q`
Expected: build FAILS — `The name 'FilterLookups' does not exist in the current context`.

- [ ] **Step 3: The smart-value catalog**

Create `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SmartSprints.cs`:

```csharp
namespace JiraUnpacked.Infrastructure.Reporting.Filtering;

/// <summary>
/// Sprint values resolved from Sprint.State at query time rather than by name. On the wire a
/// smart value is a JSON OBJECT, {"smart": "active"} — never a string, so it cannot collide with
/// a sprint someone named "active". Keys are exact; states are compared exactly as Jira sends
/// them (lower case).
/// </summary>
internal static class SmartSprints
{
    internal sealed record Smart(string Key, string Label, IReadOnlyList<string> States);

    /// <summary>In the order the picker offers them.</summary>
    internal static readonly IReadOnlyList<Smart> All = new[]
    {
        new Smart("active", "Active sprint", new[] { "active" }),
        new Smart("future", "Future sprints", new[] { "future" }),
        new Smart("open", "Open sprints (active + future)", new[] { "active", "future" }),
        new Smart("closed", "Closed sprints", new[] { "closed" }),
    };

    internal static Smart? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}
```

- [ ] **Step 4: Lookup keys, and smart values in the dictionary builder**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`, replace the `DictNameResolver` doc comment and declaration with:

```csharp
/// <summary>
/// Resolves display names to surrogate Ids (tenant-scoped, injected by the service). `field` is a
/// dict field's name — or one of the closed <see cref="FilterLookups"/> keys, for lookups that
/// are not by display name. Nothing a caller sends ever becomes a lookup key.
/// </summary>
public delegate Task<IReadOnlyList<Guid>> DictNameResolver(
    string field, IReadOnlyList<string> names, CancellationToken ct);

/// <summary>
/// Lookups the translator asks the resolver for besides display names. A closed set: the
/// compiler's resolver has an arm for each, over the same tenant-scoped context as the names.
/// </summary>
public static class FilterLookups
{
    /// <summary>names = sprint states ("active", "future", "closed") → ids of the sprints in them.</summary>
    public const string SprintState = "sprint.state";
}
```

Replace the whole `BuildDictAsync` method with:

```csharp
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildDictAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;
        var p = fk.Parameters[0];
        var fkBody = fk.Body; // i.StatusId (Guid?)
        var isEmpty = Expression.Equal(fkBody, NullGuid);

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(Expression.NotEqual(fkBody, NullGuid), p), null);

        var (pick, error) = ReadDictValues(field, c);
        if (error is not null) return (null, error);

        // Names and smart values resolve separately into ONE id set: "Sprint 7 or the active
        // sprint" is a single In. In [null] has nothing to resolve, so no round trip.
        var ids = new List<Guid>();
        if (pick!.Names.Count > 0) ids.AddRange(await resolveDict(field.Name, pick.Names, ct));
        if (pick.States.Count > 0)
            ids.AddRange(await resolveDict(FilterLookups.SprintState, pick.States.Distinct().ToList(), ct));

        // i.Fk.HasValue && ids.Contains(i.Fk.Value)
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        var contains = Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);
        var inSet = Expression.AndAlso(hasValue, contains);

        return c.Op switch
        {
            // One meaning of "empty" across the product: it matches only when asked for. "Is not"
            // and "is none of" therefore require a value — a NULL null-in-NotIn adds nothing.
            FilterOperator.Neq or FilterOperator.NotIn =>
                (Lambda(Expression.AndAlso(hasValue, Expression.Not(contains)), p), null),
            FilterOperator.In when pick.WantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }

    /// <summary>
    /// What a dictionary condition asked for: display names, sprint states (from smart values), and
    /// whether (Empty) — a JSON null inside 'values' — was among them.
    /// </summary>
    private sealed record DictPick(List<string> Names, List<string> States, bool WantsEmpty);

    private static (DictPick?, string?) ReadDictValues(FilterField field, FilterCondition c)
    {
        var pick = new DictPick(new List<string>(), new List<string>(), false);

        if (c.Op is FilterOperator.In or FilterOperator.NotIn)
        {
            if (c.Values is null) return (null, $"Field '{c.Field}' with '{c.Op}' requires 'values'.");
            if (c.Values.Count > MaxInValues) return (null, $"'{c.Op}' accepts at most {MaxInValues} values.");
            var wantsEmpty = false;
            foreach (var v in c.Values)
            {
                switch (v.ValueKind)
                {
                    case JsonValueKind.Null:
                        wantsEmpty = true;
                        break;
                    case JsonValueKind.String:
                        pick.Names.Add(v.GetString()!);
                        break;
                    case JsonValueKind.Object:
                        if (ReadSmart(field, v, pick.States) is { } smartError) return (null, smartError);
                        break;
                    default:
                        return (null, $"Field '{c.Field}' values must be strings, or null for (Empty).");
                }
            }
            return (pick with { WantsEmpty = wantsEmpty }, null);
        }

        // Eq / Neq: one name, or one smart value.
        if (c.Value is { ValueKind: JsonValueKind.String } sv)
        {
            pick.Names.Add(sv.GetString()!);
        }
        else if (c.Value is { ValueKind: JsonValueKind.Object } ov)
        {
            if (ReadSmart(field, ov, pick.States) is { } smartError) return (null, smartError);
        }
        else
        {
            return (null, $"Field '{c.Field}' requires a string 'value'.");
        }
        return (pick, null);
    }

    /// <summary>A smart value, {"smart": "active"}: valid on sprint only, and only with a known key.</summary>
    private static string? ReadSmart(FilterField field, JsonElement v, List<string> states)
    {
        if (field.Name != "sprint")
            return $"Smart values are only available on 'sprint', not '{field.Name}'.";
        if (!v.TryGetProperty("smart", out var key) || key.ValueKind != JsonValueKind.String)
            return "A smart value must look like {\"smart\": \"active\"}.";
        var smart = SmartSprints.Find(key.GetString()!);
        if (smart is null)
            return $"Unknown smart value '{key.GetString()}'. Use one of: {string.Join(", ", SmartSprints.All.Select(s => s.Key))}.";
        states.AddRange(smart.States);
        return null;
    }
```

- [ ] **Step 5: Resolve sprint states in the compiler**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs`, in `ResolveDictAsync`, add this arm directly before `_ => Array.Empty<Guid>(),`:

```csharp
            // Lookups beyond display names (FilterLookups). Same tenant-scoped context, so a smart
            // value can no more reach another tenant's sprints than a name can; the caller's
            // project filter on Issues keeps the match to this project's own sprints.
            FilterLookups.SprintState => await _db.Sprints.Where(x => x.State != null && names.Contains(x.State)).Select(x => x.Id).ToListAsync(ct),
```

- [ ] **Step 6: Run the unit tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SmartSprintFilterTests|FullyQualifiedName~IssueFilterTranslatorTests|FullyQualifiedName~IssueFilterGroupTests" -v q`
Expected: PASS (including the existing `Dict_in_rejects_a_value_that_is_neither_string_nor_null` and `Eq_with_a_json_null_value_is_still_an_error`).

- [ ] **Step 7: Write the failing integration tests**

In `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs`, add after `SeedIssueAsync`:

```csharp
    /// <summary>A sprint of this project in the given state ("active", "future", "closed").</summary>
    internal static async Task<Guid> SeedSprintAsync(SeedContext ctx, string name, string state)
    {
        var id = Guid.NewGuid();
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Sprints""(""Id"",""EndUserId"",""JiraProjectId"",""JiraSprintId"",""Name"",""State"")
              VALUES (@i,@t,@p,@js,@n,@s)",
            ("i", id), ("t", ctx.Tenant), ("p", ctx.ProjectId), ("js", id.ToString("N")[..12]), ("n", name), ("s", state));
        return id;
    }
```

Create `tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace JiraUnpacked.Tests.Integration;

/// <summary>Smart sprint values end to end: query, value list counts, and resolution at query time.</summary>
public sealed class SmartSprintTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public SmartSprintTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static object Smart(string key) => new { smart = key };
    private static object SprintIn(string op, params object?[] values) => new { field = "sprint", op, values };
    private static object SprintIs(string op, object value) => new { field = "sprint", op, value };

    private sealed record Seeded(HttpClient Client, SeedContext Ctx, Guid ActiveSprintId);

    /// <summary>S-A active, S-F future, S-C closed, S-N in a closed sprint NAMED "active", S-0 no sprint.</summary>
    private async Task<Seeded> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var active = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 12", "active");
        var future = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 13", "future");
        var closed = await RicherFilterTestData.SeedSprintAsync(ctx, "Sprint 11", "closed");
        var namedActive = await RicherFilterTestData.SeedSprintAsync(ctx, "active", "closed");
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-A", sprintId: active);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-F", sprintId: future);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-C", sprintId: closed);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-N", sprintId: namedActive);
        await RicherFilterTestData.SeedIssueAsync(ctx, "S-0");
        return new Seeded(client, ctx, active);
    }

    [Fact]
    public async Task Smart_values_resolve_from_sprint_state()
    {
        var s = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, c);

        Assert.Equal(new[] { "S-A" }, await Keys(SprintIn("In", Smart("active"))));
        Assert.Equal(new[] { "S-A", "S-F" }, await Keys(SprintIn("In", Smart("open"))));
        Assert.Equal(new[] { "S-C", "S-N" }, await Keys(SprintIn("In", Smart("closed"))));
        Assert.Equal(new[] { "S-F" }, await Keys(SprintIs("Eq", Smart("future"))));
        Assert.Equal(new[] { "S-A", "S-F" }, await Keys(SprintIn("NotIn", Smart("closed"))));
        Assert.Equal(new[] { "S-0", "S-A" }, await Keys(SprintIn("In", Smart("active"), null)));
    }

    [Fact]
    public async Task A_string_still_means_a_sprint_name()
    {
        var s = await SeedAsync();

        Assert.Equal(new[] { "S-N" }, await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIn("In", "active")));
        Assert.Equal(new[] { "S-N" }, await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIs("Eq", "active")));
    }

    [Fact]
    public async Task A_state_change_is_seen_without_editing_the_filter()
    {
        var s = await SeedAsync();
        await using (var conn = new NpgsqlConnection(JiraUnpackedApiFactory.TestConnectionString))
        {
            await conn.OpenAsync();
            await MetricsTestData.Exec(conn, @"UPDATE ""Sprints"" SET ""State"" = 'closed' WHERE ""Id"" = @i", ("i", s.ActiveSprintId));
        }

        // No active sprint any more: a real state, so an empty result — not an error.
        Assert.Empty(await RicherFilterTestData.KeysAsync(s.Client, s.Ctx.ProjectId, SprintIn("In", Smart("active"))));
    }

    [Fact]
    public async Task Smart_values_are_refused_off_sprint_and_when_unknown()
    {
        var s = await SeedAsync();

        Assert.Contains("only available on 'sprint'", await RicherFilterTestData.RejectionAsync(
            s.Client, s.Ctx.ProjectId, new { field = "status", op = "In", values = new[] { Smart("active") } }));
        Assert.Contains("'current'", await RicherFilterTestData.RejectionAsync(
            s.Client, s.Ctx.ProjectId, SprintIn("In", Smart("current"))));
    }

    [Fact]
    public async Task The_sprint_value_list_counts_each_smart_value_and_other_fields_have_none()
    {
        var s = await SeedAsync();

        var sprint = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Ctx.ProjectId}/filter-values?field=sprint");
        var smart = sprint.GetProperty("smart").EnumerateArray()
            .Select(x => (x.GetProperty("key").GetString(), x.GetProperty("label").GetString(), x.GetProperty("count").GetInt32()))
            .ToArray();
        Assert.Equal(new[]
        {
            ("active", "Active sprint", 1),
            ("future", "Future sprints", 1),
            ("open", "Open sprints (active + future)", 2),
            ("closed", "Closed sprints", 2),
        }, smart);

        var status = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.Ctx.ProjectId}/filter-values?field=status");
        Assert.False(status.TryGetProperty("smart", out _));
    }
}
```

- [ ] **Step 8: Run to verify the value-list test fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SmartSprintTests" -v q`
Expected: the query tests PASS; `The_sprint_value_list_counts_each_smart_value_and_other_fields_have_none` FAILS with `KeyNotFoundException` (no `smart` property yet).

- [ ] **Step 9: Count smart values in the value list**

In `src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs`, add after `FilterValueCount` and replace `FilterValuesResult`:

```csharp
/// <summary>A smart sprint value (see the design spec §2) and how many of this project's issues it matches now.</summary>
public sealed record SmartValueCount(string Key, string Label, int Count);

/// <summary>
/// Values + null Error => 200. Null Values + Error => 400. Both null => the project is unknown
/// or belongs to another tenant => 404. EmptyCount is how many of the project's issues have no
/// value for the field — the number behind the picker's (Empty) entry. Smart is set for sprint
/// only.
/// </summary>
public sealed record FilterValuesResult(
    IReadOnlyList<FilterValueCount>? Values, int EmptyCount, string? Error,
    IReadOnlyList<SmartValueCount>? Smart = null);
```

In `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`, add `using JiraUnpacked.Infrastructure.Reporting.Filtering;` and replace the final `return new FilterValuesResult(…);` with:

```csharp
        IReadOnlyList<SmartValueCount>? smart = null;
        if (field == "sprint")
        {
            // One grouped read by state; each smart value is then a sum of states ("open" is
            // active + future). Over the whole project, like every other count here.
            var byState = await issues
                .Join(_db.Sprints, i => i.SprintId, s => (Guid?)s.Id, (_, s) => s.State)
                .GroupBy(state => state)
                .Select(g => new { State = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            smart = SmartSprints.All
                .Select(s => new SmartValueCount(s.Key, s.Label,
                    byState.Where(x => x.State != null && s.States.Contains(x.State)).Sum(x => x.Count)))
                .ToList();
        }

        return new FilterValuesResult(
            rows.Select(r => new FilterValueCount(r.Name, r.Count)).ToList(), emptyCount, null, smart);
```

In `src/JiraUnpacked.Api/Controllers/IssuesController.cs`, replace the last statement of `FilterValues`:

```csharp
        if (result.Values is null) return NotFound();
        // `smart` only where it exists (sprint): its absence tells a client there is no Smart section.
        return result.Smart is null
            ? Ok(new { field, values = result.Values, emptyCount = result.EmptyCount })
            : Ok(new { field, values = result.Values, emptyCount = result.EmptyCount, smart = result.Smart });
```

- [ ] **Step 10: Run the tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~SmartSprintTests|FullyQualifiedName~FilterValuesTests|FullyQualifiedName~IssueFilterTests|FullyQualifiedName~IssueFilterGroupEndpointTests|FullyQualifiedName~IssueFilterSecurityTests" -v q`
Expected: PASS.

- [ ] **Step 11: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IFilterValueService.cs src/JiraUnpacked.Infrastructure/Reporting src/JiraUnpacked.Api/Controllers/IssuesController.cs tests/JiraUnpacked.Tests/Reporting/SmartSprintFilterTests.cs tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs tests/JiraUnpacked.Tests/Integration/SmartSprintTests.cs
git commit -m "feat(filters): smart sprint values resolved from sprint state, with counts in the value list" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Status category — filter field, value list, column, sort and export (server)

**Files:**
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs` (`FilterLookups`, dispatch, `BuildStatusCategoryAsync`)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs` (`ResolveDictAsync`)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SortFields.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`
- Modify: `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs`
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs` (`ToListItem`, `ApplySort`)
- Modify: `src/JiraUnpacked.Infrastructure/Reporting/ExportColumns.cs`
- Modify: `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`
- Modify: `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs` (+ `SeedStatusAsync`)
- Modify: `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs` (one test)
- Create: `tests/JiraUnpacked.Tests/Reporting/StatusCategoryFilterTests.cs`
- Create: `tests/JiraUnpacked.Tests/Integration/StatusCategoryTests.cs`

**Interfaces:**
- Consumes: `DictNameResolver`, `FilterLookups` (Task 3), `ReadDictValues`/`DictPick` (Task 3, private to the translator).
- Produces: `internal enum FieldType { DictName, Decimal, DateTime, Text, ParentRef, StatusCategory }`; catalog field `statusCategory`, type `"StatusCategory"`, operators `Eq, Neq, In, NotIn, IsNull, IsNotNull`.
- Produces: `FilterLookups.StatusCategory = "statusCategory"` (names = category names → ids of statuses in them) and `FilterLookups.UncategorisedStatus = "statusCategory.none"` (names ignored → ids of statuses whose category is null or blank).
- Produces: `IssueListItem(…, DateTime JiraUpdatedAt, string? StatusCategory)` — appended last; JSON `statusCategory`.
- Produces: sort name `statusCategory`; export column id `statusCategory`, header `Category`, directly after `status`.
- Produces (tests): `RicherFilterTestData.SeedStatusAsync(SeedContext ctx, string name, string? category) : Task<Guid>`.

- [ ] **Step 1: Write the failing translator tests**

Create `tests/JiraUnpacked.Tests/Reporting/StatusCategoryFilterTests.cs`:

```csharp
using System.Text.Json;
using JiraUnpacked.Domain.Abstractions;
using JiraUnpacked.Domain.Entities;
using JiraUnpacked.Infrastructure.Reporting.Filtering;
using JiraUnpacked.Tests.Security;

namespace JiraUnpacked.Tests.Reporting;

/// <summary>
/// statusCategory filters through the status: category names → the statuses in them →
/// StatusId in set. (Empty) is no status, or a status with no category.
/// </summary>
public sealed class StatusCategoryFilterTests
{
    private static readonly Guid ToDoId = Guid.NewGuid();    // a status in "To Do"
    private static readonly Guid DoingId = Guid.NewGuid();   // a status in "In Progress"
    private static readonly Guid DoneId = Guid.NewGuid();    // a status in "Done"
    private static readonly Guid LimboId = Guid.NewGuid();   // a status with no category

    private static readonly IIssueFilterTranslator Sut = new IssueFilterTranslator(new FakeClock(DateTime.UtcNow));

    private static JsonElement J(object? v) => JsonSerializer.SerializeToElement(v);

    private static Task<IReadOnlyList<Guid>> Resolver(string lookup, IReadOnlyList<string> names, CancellationToken ct)
    {
        var byCategory = new Dictionary<string, Guid> { ["To Do"] = ToDoId, ["In Progress"] = DoingId, ["Done"] = DoneId };
        IReadOnlyList<Guid> ids = lookup switch
        {
            FilterLookups.StatusCategory => names.Where(byCategory.ContainsKey).Select(n => byCategory[n]).ToList(),
            FilterLookups.UncategorisedStatus => new[] { LimboId },
            _ => Array.Empty<Guid>(),
        };
        return Task.FromResult(ids);
    }

    private static async Task<Func<Issue, bool>> Pred(FilterCondition c)
    {
        var r = await Sut.BuildAsync(new[] { c }, Resolver, CancellationToken.None);
        Assert.Null(r.Error);
        return r.Predicate!.Compile();
    }

    private static Issue With(Guid? statusId) => new()
    {
        Id = Guid.NewGuid(), JiraIssueId = "1", JiraKey = "A-1", StatusId = statusId,
        JiraCreatedAt = DateTime.UtcNow, JiraUpdatedAt = DateTime.UtcNow,
    };

    private static FilterCondition Is(FilterOperator op, string name) => new("statusCategory", op, J(name), null);
    private static FilterCondition Any(FilterOperator op, params string?[] names) =>
        new("statusCategory", op, null, names.Select(n => J(n)).ToList());

    [Fact]
    public async Task Eq_matches_the_statuses_in_that_category_only()
    {
        var pred = await Pred(Is(FilterOperator.Eq, "Done"));

        Assert.True(pred(With(DoneId)));
        Assert.False(pred(With(ToDoId)));
        Assert.False(pred(With(LimboId)));
        Assert.False(pred(With(null)));
    }

    [Fact]
    public async Task Neq_and_NotIn_never_match_empty()
    {
        var neq = await Pred(Is(FilterOperator.Neq, "Done"));
        Assert.True(neq(With(ToDoId)));
        Assert.True(neq(With(DoingId)));
        Assert.False(neq(With(DoneId)));
        Assert.False(neq(With(LimboId)));   // a status with no category is (Empty), not "not Done"
        Assert.False(neq(With(null)));

        var notIn = await Pred(Any(FilterOperator.NotIn, "Done"));
        Assert.True(notIn(With(ToDoId)));
        Assert.False(notIn(With(DoneId)));
        Assert.False(notIn(With(LimboId)));
        Assert.False(notIn(With(null)));
    }

    [Fact]
    public async Task In_matches_empty_only_when_asked_for()
    {
        var plain = await Pred(Any(FilterOperator.In, "To Do", "Done"));
        Assert.True(plain(With(ToDoId)));
        Assert.True(plain(With(DoneId)));
        Assert.False(plain(With(DoingId)));
        Assert.False(plain(With(LimboId)));
        Assert.False(plain(With(null)));

        var withEmpty = await Pred(Any(FilterOperator.In, "Done", null));
        Assert.True(withEmpty(With(DoneId)));
        Assert.True(withEmpty(With(LimboId)));
        Assert.True(withEmpty(With(null)));
        Assert.False(withEmpty(With(ToDoId)));
    }

    [Fact]
    public async Task IsNull_is_no_status_or_no_category_and_IsNotNull_is_the_rest()
    {
        var isNull = await Pred(new FilterCondition("statusCategory", FilterOperator.IsNull, null, null));
        Assert.True(isNull(With(null)));
        Assert.True(isNull(With(LimboId)));
        Assert.False(isNull(With(DoneId)));

        var isNotNull = await Pred(new FilterCondition("statusCategory", FilterOperator.IsNotNull, null, null));
        Assert.True(isNotNull(With(DoneId)));
        Assert.False(isNotNull(With(LimboId)));
        Assert.False(isNotNull(With(null)));
    }

    [Fact]
    public async Task Smart_values_and_other_operators_are_refused()
    {
        var smart = await Sut.BuildAsync(new[] { new FilterCondition("statusCategory", FilterOperator.In, null,
            new List<JsonElement> { J(new { smart = "active" }) }) }, Resolver, CancellationToken.None);
        Assert.Contains("only available on 'sprint'", smart.Error);

        var contains = await Sut.BuildAsync(new[] { new FilterCondition("statusCategory", FilterOperator.Contains, J("Do"), null) },
            Resolver, CancellationToken.None);
        Assert.Equal("Operator 'Contains' is not allowed for field 'statusCategory'.", contains.Error);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~StatusCategoryFilterTests" -v q`
Expected: build FAILS — `'FilterLookups' does not contain a definition for 'StatusCategory'`.

- [ ] **Step 3: The field, its lookups and its builder**

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/FilterField.cs`:

```csharp
internal enum FieldType { DictName, Decimal, DateTime, Text, ParentRef, StatusCategory }
```

and add this entry directly after the `status` entry in `FilterFields.All`:

```csharp
            // Filters through the status (StatusId), but by the status's CATEGORY — its own type,
            // because its (Empty) also covers a status that has no category.
            ["statusCategory"] = new("statusCategory", FieldType.StatusCategory,
                                 Ops(FilterOperator.Eq, FilterOperator.Neq, FilterOperator.In, FilterOperator.NotIn,
                                     FilterOperator.IsNull, FilterOperator.IsNotNull),
                                 (Expression<Func<Issue, Guid?>>)(i => i.StatusId)),
```

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterTranslator.cs`:

(a) Add to `FilterLookups`, after `SprintState`:

```csharp

    /// <summary>names = status category names → ids of the statuses in those categories.</summary>
    public const string StatusCategory = "statusCategory";

    /// <summary>names ignored → ids of the statuses with no category (null or blank).</summary>
    public const string UncategorisedStatus = "statusCategory.none";
```

(b) In `BuildConditionAsync`'s switch, add after the `FieldType.DictName` arm:

```csharp
            FieldType.StatusCategory => await BuildStatusCategoryAsync(field, c, resolveDict, ct),
```

(c) Add after `ReadSmart`:

```csharp
    /// <summary>
    /// Status category, through the status: category names → the ids of the statuses in those
    /// categories → StatusId in set. No join in the issue query. (Empty) is an issue with no
    /// status OR whose status has no category — both answer "which category?" with nothing, and
    /// the empty rule applies to that meaning exactly as it does to every dictionary field.
    /// </summary>
    private static async Task<(Expression<Func<Issue, bool>>?, string?)> BuildStatusCategoryAsync(
        FilterField field, FilterCondition c, DictNameResolver resolveDict, CancellationToken ct)
    {
        var fk = (Expression<Func<Issue, Guid?>>)field.Selector;   // i.StatusId
        var p = fk.Parameters[0];
        var fkBody = fk.Body;
        var hasValue = Expression.Property(fkBody, nameof(Nullable<Guid>.HasValue));
        var value = Expression.Property(fkBody, nameof(Nullable<Guid>.Value));
        Expression IdIn(List<Guid> ids) => Expression.Call(Expression.Constant(ids), ListContains<Guid>(), value);

        // Read first, so a malformed condition is refused before any lookup runs.
        DictPick? pick = null;
        if (c.Op is not (FilterOperator.IsNull or FilterOperator.IsNotNull))
        {
            var (read, error) = ReadDictValues(field, c);
            if (error is not null) return (null, error);
            pick = read;
        }

        var uncategorised = (await resolveDict(FilterLookups.UncategorisedStatus, Array.Empty<string>(), ct)).ToList();
        // AndAlso/OrElse short-circuit before .Value in memory; in SQL they are plain NULL checks.
        var isEmpty = Expression.OrElse(Expression.Equal(fkBody, NullGuid), IdIn(uncategorised));
        var categorised = Expression.AndAlso(hasValue, Expression.Not(IdIn(uncategorised)));

        if (c.Op == FilterOperator.IsNull) return (Lambda(isEmpty, p), null);
        if (c.Op == FilterOperator.IsNotNull) return (Lambda(categorised, p), null);

        var ids = pick!.Names.Count == 0
            ? new List<Guid>()
            : (await resolveDict(FilterLookups.StatusCategory, pick.Names, ct)).ToList();
        var inSet = Expression.AndAlso(hasValue, IdIn(ids));

        return c.Op switch
        {
            // "Is not Done" = has a category, and it is not Done.
            FilterOperator.Neq or FilterOperator.NotIn =>
                (Lambda(Expression.AndAlso(categorised, Expression.Not(IdIn(ids))), p), null),
            FilterOperator.In when pick.WantsEmpty => (Lambda(Expression.OrElse(isEmpty, inSet), p), null),
            _ => (Lambda(inSet, p), null),   // Eq, In
        };
    }
```

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/IssueFilterCompiler.cs`, add after the `FilterLookups.SprintState` arm:

```csharp
            FilterLookups.StatusCategory => await _db.Statuses.Where(x => x.StatusCategory != null && names.Contains(x.StatusCategory)).Select(x => x.Id).ToListAsync(ct),
            FilterLookups.UncategorisedStatus => await _db.Statuses.Where(x => x.StatusCategory == null || x.StatusCategory == "").Select(x => x.Id).ToListAsync(ct),
```

- [ ] **Step 4: Run the unit tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~StatusCategoryFilterTests|FullyQualifiedName~SmartSprintFilterTests|FullyQualifiedName~IssueFilterTranslatorTests" -v q`
Expected: PASS.

- [ ] **Step 5: Write the failing end-to-end tests**

In `tests/JiraUnpacked.Tests/Integration/RicherFilterTestData.cs`, add after `SeedSprintAsync`:

```csharp
    /// <summary>A status on this project's connection with the given category (null = none).</summary>
    internal static async Task<Guid> SeedStatusAsync(SeedContext ctx, string name, string? category)
    {
        var id = Guid.NewGuid();
        await using var conn = Conn();
        await conn.OpenAsync();
        await MetricsTestData.Exec(conn,
            @"INSERT INTO ""Statuses""(""Id"",""EndUserId"",""JiraConnectionId"",""JiraStatusId"",""Name"",""StatusCategory"")
              VALUES (@i,@t,@c,@sid,@n,@cat)",
            ("i", id), ("t", ctx.Tenant), ("c", ctx.ConnectionId), ("sid", id.ToString("N")[..8]), ("n", name), ("cat", category));
        return id;
    }
```

Create `tests/JiraUnpacked.Tests/Integration/StatusCategoryTests.cs`:

```csharp
// tests/JiraUnpacked.Tests/Integration/StatusCategoryTests.cs
using System.Net.Http.Json;
using JiraUnpacked.Domain.Abstractions;

namespace JiraUnpacked.Tests.Integration;

/// <summary>
/// statusCategory end to end. SeedProjectAsync's statuses carry the categories "new" (To Do),
/// "indeterminate" (In Progress) and "done" (Done) — shown as stored, whatever Jira sent.
/// </summary>
public sealed class StatusCategoryTests : IClassFixture<JiraUnpackedApiFactory>, IAsyncLifetime
{
    private readonly JiraUnpackedApiFactory _factory;
    public StatusCategoryTests(JiraUnpackedApiFactory f) => _factory = f;
    public Task InitializeAsync() => _factory.EnsureDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ValueDto(string Name, int Count);
    private sealed record ValuesDto(string Field, List<ValueDto> Values, int EmptyCount);

    private static object Is(string op, string value) => new { field = "statusCategory", op, value };
    private static object Any(string op, params string?[] values) => new { field = "statusCategory", op, values };
    private static object Empty(string op) => new { field = "statusCategory", op };

    /// <summary>C-T To Do, C-P In Progress, C-D Done, C-L a status with no category, C-0 no status.</summary>
    private async Task<(HttpClient client, SeedContext ctx)> SeedAsync()
    {
        var (client, tenant) = await MetricsTestData.AuthedTenantAsync(_factory);
        var ctx = await MetricsTestData.SeedProjectAsync(tenant);
        var limbo = await RicherFilterTestData.SeedStatusAsync(ctx, "Limbo", null);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-T", statusId: ctx.ToDoStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-P", statusId: ctx.InProgressStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-D", statusId: ctx.DoneStatusId);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-L", statusId: limbo);
        await RicherFilterTestData.SeedIssueAsync(ctx, "C-0");
        return (client, ctx);
    }

    [Fact]
    public async Task Filters_by_category_with_the_empty_rule()
    {
        var (client, ctx) = await SeedAsync();
        Task<string[]> Keys(object c) => RicherFilterTestData.KeysAsync(client, ctx.ProjectId, c);

        Assert.Equal(new[] { "C-T" }, await Keys(Is("Eq", "new")));
        Assert.Equal(new[] { "C-D", "C-P" }, await Keys(Is("Neq", "new")));
        Assert.Equal(new[] { "C-0", "C-D", "C-L" }, await Keys(Any("In", "done", null)));
        Assert.Equal(new[] { "C-P", "C-T" }, await Keys(Any("NotIn", "done")));
        Assert.Equal(new[] { "C-0", "C-L" }, await Keys(Empty("IsNull")));
        Assert.Equal(new[] { "C-D", "C-P", "C-T" }, await Keys(Empty("IsNotNull")));
    }

    [Fact]
    public async Task The_value_list_counts_categories_and_both_kinds_of_empty()
    {
        var (client, ctx) = await SeedAsync();

        var body = await client.GetFromJsonAsync<ValuesDto>($"/api/projects/{ctx.ProjectId}/filter-values?field=statusCategory");

        Assert.Equal(new[] { new ValueDto("done", 1), new ValueDto("indeterminate", 1), new ValueDto("new", 1) }, body!.Values);
        Assert.Equal(2, body.EmptyCount);   // C-L (no category) and C-0 (no status)
    }

    [Fact]
    public async Task The_list_carries_the_category_and_sorts_by_it()
    {
        var (client, ctx) = await SeedAsync();

        var resp = await client.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/issues/query",
            new { sortBy = "statusCategory", sortDescending = false });
        var page = (await resp.Content.ReadFromJsonAsync<PagedResult<IssueListItem>>())!;

        Assert.Equal(new[] { "C-D", "C-P", "C-T" }, page.Items.Take(3).Select(i => i.JiraKey));
        Assert.Equal("done", page.Items.Single(i => i.JiraKey == "C-D").StatusCategory);
        Assert.Null(page.Items.Single(i => i.JiraKey == "C-L").StatusCategory);
    }

    [Fact]
    public async Task The_export_has_a_category_column()
    {
        var (client, ctx) = await SeedAsync();

        var csv = await client.GetStringAsync(
            $"/api/projects/{ctx.ProjectId}/issues/export.csv?cols=jiraKey,statusCategory&sortBy=jiraKey&sortDescending=false");

        Assert.StartsWith("Key,Category\r\n", csv);
        Assert.Contains("C-D,done\r\n", csv);
    }
}
```

In `tests/JiraUnpacked.Tests/Integration/SavedFilterTests.cs`, add this test at the end of the class:

```csharp
    [Fact]
    public async Task A_view_stores_object_values_and_may_show_and_sort_by_category()
    {
        // The catalog is the column vocabulary, so a new field is a valid column the moment it
        // exists; and object values round-trip through the jsonb definition untouched.
        var (client, projectId) = await SeedAsync();

        var created = await CreateOkAsync(client, projectId, new
        {
            name = "Recent, by category",
            filter = new
            {
                logic = "And",
                conditions = new object[]
                {
                    new { field = "updatedAt", op = "InPeriod", value = new { period = "thisWeek", timeZone = "Europe/Warsaw" } },
                    new { field = "sprint", op = "In", values = new object?[] { new { smart = "open" }, null } },
                },
            },
            columns = new[] { "jiraKey", "statusCategory" },
            sortBy = "statusCategory",
        });

        var stored = created.Filter!.Conditions!;
        Assert.Equal("Europe/Warsaw", stored[0].Value!.Value.GetProperty("timeZone").GetString());
        Assert.Equal("open", stored[1].Values![0].GetProperty("smart").GetString());
        Assert.Equal(JsonValueKind.Null, stored[1].Values![1].ValueKind);
        Assert.Equal(new[] { "jiraKey", "statusCategory" }, created.Columns);
        Assert.Equal("statusCategory", created.SortBy);
    }
```

- [ ] **Step 6: Run to verify they fail**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~StatusCategoryTests|FullyQualifiedName~SavedFilterTests" -v q`
Expected: build FAILS — `'IssueListItem' does not contain a definition for 'StatusCategory'`.

- [ ] **Step 7: Values, projection, sort and export**

In `src/JiraUnpacked.Infrastructure/Reporting/FilterValueService.cs`, add this arm directly after the `"status"` arm of the `source` switch:

```csharp
            // A category is reached through the status. Blank counts as none, as in the filter.
            // (Empty) is an issue with no status OR whose status has no category — the same two
            // cases the filter's empty rule matches.
            "statusCategory" => (
                issues.Join(_db.Statuses.Where(s => s.StatusCategory != null && s.StatusCategory != ""),
                            i => i.StatusId, d => (Guid?)d.Id, (_, d) => d.StatusCategory!),
                issues.Where(i => i.StatusId == null || !_db.Statuses.Any(
                    s => s.Id == i.StatusId && s.StatusCategory != null && s.StatusCategory != ""))),
```

and change the error message's list to `"status, statusCategory, issueType, priority, assignee, sprint, team."`.

In `src/JiraUnpacked.Domain/Abstractions/IssueViews.cs`, replace `IssueListItem`:

```csharp
/// <summary>StatusCategory is the status's category name as Jira sent it (e.g. "To Do"); null when there is no status or it has none.</summary>
public sealed record IssueListItem(
    string JiraIssueId, string JiraKey, string? Summary,
    string? Status, string? IssueType, string? Priority, string? Assignee, string? Sprint, string? Team,
    decimal? StoryPoints, string? ParentJiraIssueId, DateTime JiraCreatedAt, DateTime JiraUpdatedAt,
    string? StatusCategory);
```

In `src/JiraUnpacked.Infrastructure/Reporting/IssueQueryService.cs`, replace the last line of `ToListItem`:

```csharp
        i.StoryPoints, i.ParentJiraIssueId, i.JiraCreatedAt, i.JiraUpdatedAt,
        _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.StatusCategory).FirstOrDefault());
```

and add this arm to `ApplySort`'s switch directly after `"status"`:

```csharp
            "statusCategory" => By(i => _db.Statuses.Where(s => s.Id == i.StatusId).Select(s => s.StatusCategory).FirstOrDefault()),
```

In `src/JiraUnpacked.Infrastructure/Reporting/Filtering/SortFields.cs`:

```csharp
    internal static readonly IReadOnlyList<string> Names = new[]
    {
        "jiraKey", "summary", "status", "statusCategory", "issueType", "priority", "assignee",
        "sprint", "team", "storyPoints", "createdAt", "updatedAt",
    };
```

In `src/JiraUnpacked.Infrastructure/Reporting/ExportColumns.cs`, add directly after the `status` column:

```csharp
        new Column("statusCategory", "Category", i => i.StatusCategory),
```

- [ ] **Step 8: Pin the catalog**

In `tests/JiraUnpacked.Tests/Reporting/FilterCatalogServiceTests.cs`, add to `Operators_are_listed_in_reading_order_not_declaration_order`:

```csharp
        Assert.Equal(new[] { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }, OpsOf("statusCategory"));
```

In `tests/JiraUnpacked.Tests/Integration/FilterCatalogEndpointTests.cs`: add `"statusCategory"` to `ExpectedFieldNames` (after `"status"`), add `["statusCategory"] = "StatusCategory",` to `ExpectedFieldTypes`, change `Assert.Equal(12, catalog!.Fields.Count);` to `Assert.Equal(13, catalog!.Fields.Count);` (and "All twelve fields" in its comment to "All thirteen fields"), and add after the `status` operator assertion:

```csharp
        var statusCategory = catalog.Fields.Single(f => f.Name == "statusCategory");
        Assert.True(new HashSet<string> { "Eq", "Neq", "In", "NotIn", "IsNull", "IsNotNull" }.SetEquals(statusCategory.Operators));
```

- [ ] **Step 9: Run the tests**

Run: `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test tests/JiraUnpacked.Tests/JiraUnpacked.Tests.csproj --filter "FullyQualifiedName~StatusCategory|FullyQualifiedName~SavedFilterTests|FullyQualifiedName~FilterCatalog|FullyQualifiedName~IssueSortTests|FullyQualifiedName~IssueExportTests|FullyQualifiedName~IssueListTests|FullyQualifiedName~IssueDetailTests|FullyQualifiedName~FilterValuesTests" -v q`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Domain/Abstractions/IssueViews.cs src/JiraUnpacked.Infrastructure/Reporting tests/JiraUnpacked.Tests
git commit -m "feat(filters): status category — filter field, value list, Category column, sort and export" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Richer values in the SPA model (wire types, readers, completeness, operator switching)

**Files:**
- Modify: `src/JiraUnpacked.Web/src/api/issues.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/valueShapes.ts`
- Create: `src/JiraUnpacked.Web/src/features/issues/valueShapes.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` (handlers, scalar narrowing, fallback)
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx` (`MultiValuePicker` takes smart values)
- Modify: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx` (`row()` fixture), `src/JiraUnpacked.Web/src/features/issues/IssueDetail.test.tsx` (`detail()` fixture)

**Interfaces:**
- Consumes (HTTP, Tasks 2–4): the new operators and field in `GET /api/filter-catalog`; `smart` on `filter-values?field=sprint`; `statusCategory` on list items.
- Produces (`api/issues.ts`): `FilterOperator` + `'Between' | 'InLast' | 'NotInLast' | 'InPeriod'`; `FieldType` + `'StatusCategory'`; `RelativeUnit`, `RelativeAmount { amount: number; unit: RelativeUnit }`, `PeriodKey`, `PeriodValue { period: PeriodKey; timeZone?: string }`, `SmartSprintKey`, `SmartValue { smart: SmartSprintKey }`, `ListValue = string | number | null | SmartValue`, `ConditionValue = string | number | null | SmartValue | RelativeAmount | PeriodValue`; `FilterCondition.value?: ConditionValue`, `.values?: ListValue[]`; `IssueListItem.statusCategory: string | null`; `SmartValueOption { key: SmartSprintKey; label: string; count: number }`; `FilterValues.smart?: SmartValueOption[]`.
- Produces (`valueShapes.ts`): `RELATIVE_UNITS`, `PERIODS: readonly { key: PeriodKey; label: string }[]`, `SMART_SPRINT_LABELS: Record<SmartSprintKey, string>`, `DEFAULT_RELATIVE`, `DEFAULT_PERIOD`, `readSmart(v): SmartValue | undefined`, `readRelativeAmount(v): RelativeAmount | undefined`, `readPeriod(v): PeriodValue | undefined`, `isSmartKey(k: string): k is SmartSprintKey`, `isSmartValue`, `isRelativeAmount`, `isPeriodValue` (type guards), `isScalar(v): v is string | number`, `smartLabel(v: SmartValue): string`, `browserTimeZone(): string`.
- Produces (`filterTree.ts`): `takesRange(op)`, `takesAmount(op)`, `takesPeriod(op)`, `freshCondition(field, op, timeZone): FilterCondition`, `switchOperator(c, nextOp, timeZone): FilterCondition`; `isCompleteCondition`/`toWireCondition`/`parseFilterParam` understand the object values.
- Produces (`ValuePicker.tsx`): `MultiValuePicker` props `selected: ListValue[]`, `onChange: (next: ListValue[]) => void`.

- [ ] **Step 1: Wire types**

In `src/JiraUnpacked.Web/src/api/issues.ts`, replace the `FilterOperator` and `FieldType` declarations:

```ts
export type FilterOperator =
  | 'Eq' | 'Neq' | 'In' | 'NotIn' | 'Lt' | 'Lte' | 'Gt' | 'Gte'
  | 'Contains' | 'NotContains' | 'StartsWith' | 'IsNull' | 'IsNotNull'
  | 'Between' | 'InLast' | 'NotInLast' | 'InPeriod';

export type FieldType = 'DictName' | 'Decimal' | 'DateTime' | 'Text' | 'ParentRef' | 'StatusCategory';

/** "in the last" / "more than … ago" take `value: { amount, unit }`. Months are calendar months. */
export type RelativeUnit = 'hours' | 'days' | 'weeks' | 'months';
export interface RelativeAmount {
  amount: number;
  unit: RelativeUnit;
}

/** "in period" takes `value: { period, timeZone }`. Weeks start Monday; the zone is IANA, missing = UTC. */
export type PeriodKey =
  | 'today' | 'yesterday' | 'thisWeek' | 'lastWeek' | 'thisMonth' | 'lastMonth'
  | 'thisQuarter' | 'lastQuarter' | 'thisYear' | 'lastYear';
export interface PeriodValue {
  period: PeriodKey;
  timeZone?: string;
}

/**
 * A sprint resolved from its state when the query runs. An object, never a string, so it cannot
 * collide with a sprint someone NAMED "active".
 */
export type SmartSprintKey = 'active' | 'future' | 'open' | 'closed';
export interface SmartValue {
  smart: SmartSprintKey;
}

/** An element of `values`: a name or number, null for (Empty), or a smart sprint value. Between: [from, to], null = open. */
export type ListValue = string | number | null | SmartValue;

/** `value`: a name, number or date — null for (Empty) in the editor only — or one of the object shapes above. */
export type ConditionValue = string | number | null | SmartValue | RelativeAmount | PeriodValue;
```

Replace the `FilterCondition` interface and its comment:

```ts
/**
 * Mirrors the API's FilterCondition. `value` and `values` are mutually exclusive: single-value
 * operators send `value` (an object for InLast/NotInLast/InPeriod, or a smart sprint value on
 * Eq/Neq), `In`/`NotIn` send `values` (null = (Empty)), `Between` sends `values: [from, to]`
 * (null = open), and `IsNull`/`IsNotNull` send neither. A `value` of null exists only in the
 * editor (Eq/Neq on (Empty)); pruneTree turns it into IsNull/IsNotNull before anything is sent.
 * `disabled` parts are kept in the URL and skipped by the server.
 */
export interface FilterCondition {
  field: string;
  op: FilterOperator;
  value?: ConditionValue;
  values?: ListValue[];
  disabled?: boolean;
}
```

In `IssueListItem`, add after `status`:

```ts
  /** The status's category as Jira names it ("To Do", "In Progress", "Done"); null when there is none. */
  statusCategory: string | null;
```

Replace `FilterValues` (and add `SmartValueOption` before it):

```ts
/** A smart sprint value and how many of this project's issues it matches now. */
export interface SmartValueOption {
  key: SmartSprintKey;
  label: string;
  count: number;
}

export interface FilterValues {
  field: string;
  values: FilterValueOption[];
  /** Issues in this project with no value for the field: the count behind (Empty). */
  emptyCount: number;
  /** Sprint only: the smart values, in the order to offer them. */
  smart?: SmartValueOption[];
}
```

- [ ] **Step 2: Write the failing value-shape tests**

Create `src/JiraUnpacked.Web/src/features/issues/valueShapes.test.ts`:

```ts
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  PERIODS, SMART_SPRINT_LABELS, browserTimeZone, readPeriod, readRelativeAmount, readSmart,
} from './valueShapes';

afterEach(() => vi.restoreAllMocks());

describe('readers', () => {
  it('read each object shape into a fresh object in canonical key order', () => {
    const period = readPeriod({ timeZone: 'Europe/Warsaw', extra: 1, period: 'thisWeek' });
    expect(period).toEqual({ period: 'thisWeek', timeZone: 'Europe/Warsaw' });
    expect(Object.keys(period!)).toEqual(['period', 'timeZone']);

    expect(readRelativeAmount({ unit: 'weeks', amount: 2 })).toEqual({ amount: 2, unit: 'weeks' });
    expect(readSmart({ smart: 'open', label: 'x' })).toEqual({ smart: 'open' });
  });

  it('leave a period without a zone zoneless — it means UTC and keeps meaning it', () => {
    expect(readPeriod({ period: 'today' })).toEqual({ period: 'today' });
    expect(readPeriod({ period: 'today', timeZone: null })).toEqual({ period: 'today' });
  });

  it('refuse anything that is not exactly the shape', () => {
    expect(readSmart({ smart: 'current' })).toBeUndefined();
    expect(readSmart({ smart: 'toString' })).toBeUndefined();
    expect(readSmart('active')).toBeUndefined();
    expect(readRelativeAmount({ amount: '14', unit: 'days' })).toBeUndefined();
    expect(readRelativeAmount({ amount: 14, unit: 'years' })).toBeUndefined();
    expect(readPeriod({ period: 'fortnight' })).toBeUndefined();
    expect(readPeriod(['thisWeek'])).toBeUndefined();
    expect(readPeriod(null)).toBeUndefined();
  });
});

describe('vocabularies', () => {
  it('lists the periods in reading order, Today to Last year', () => {
    expect(PERIODS.map((p) => p.label)).toEqual([
      'Today', 'Yesterday', 'This week', 'Last week', 'This month', 'Last month',
      'This quarter', 'Last quarter', 'This year', 'Last year',
    ]);
  });

  it('names the smart values as the spec does', () => {
    expect(SMART_SPRINT_LABELS).toEqual({
      active: 'Active sprint', future: 'Future sprints', open: 'Open sprints (active + future)', closed: 'Closed sprints',
    });
  });
});

describe('browserTimeZone', () => {
  it('is the browser’s own IANA zone', () => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone: 'Europe/Warsaw' });

    expect(browserTimeZone()).toBe('Europe/Warsaw');
  });

  it('falls back to UTC when the browser cannot say', () => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone: '' });

    expect(browserTimeZone()).toBe('UTC');
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/valueShapes.test.ts`
Expected: FAIL — `Failed to resolve import "./valueShapes"`.

- [ ] **Step 3: Implement `valueShapes.ts`**

Create `src/JiraUnpacked.Web/src/features/issues/valueShapes.ts`:

```ts
import type {
  ConditionValue, ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit, SmartSprintKey, SmartValue,
} from '../../api/issues';

/**
 * The object-shaped filter values: a relative amount ("in the last" / "more than … ago"), a
 * calendar period ("in period") and a smart sprint value. Their vocabularies, labels and
 * defaults, and the readers that turn untrusted JSON — a link, a stored view — into exactly
 * these shapes, as fresh objects in one key order, or into nothing.
 */

export const RELATIVE_UNITS: readonly RelativeUnit[] = ['hours', 'days', 'weeks', 'months'];

/** Reading order: the order the period select offers them in. */
export const PERIODS: readonly { key: PeriodKey; label: string }[] = [
  { key: 'today', label: 'Today' },
  { key: 'yesterday', label: 'Yesterday' },
  { key: 'thisWeek', label: 'This week' },
  { key: 'lastWeek', label: 'Last week' },
  { key: 'thisMonth', label: 'This month' },
  { key: 'lastMonth', label: 'Last month' },
  { key: 'thisQuarter', label: 'This quarter' },
  { key: 'lastQuarter', label: 'Last quarter' },
  { key: 'thisYear', label: 'This year' },
  { key: 'lastYear', label: 'Last year' },
];

/** In the order the pickers offer them. The server sends the same labels; these read right before it answers. */
export const SMART_SPRINT_LABELS: Record<SmartSprintKey, string> = {
  active: 'Active sprint',
  future: 'Future sprints',
  open: 'Open sprints (active + future)',
  closed: 'Closed sprints',
};

/** "in the last" starts at 14 days, so choosing the operator already filters. */
export const DEFAULT_RELATIVE: RelativeAmount = { amount: 14, unit: 'days' };

/** "in period" likewise starts somewhere useful. */
export const DEFAULT_PERIOD: PeriodKey = 'thisWeek';

const isRecord = (v: unknown): v is Record<string, unknown> =>
  typeof v === 'object' && v !== null && !Array.isArray(v);

const PERIOD_KEYS: ReadonlySet<string> = new Set(PERIODS.map((p) => p.key));

export const isSmartKey = (k: string): k is SmartSprintKey =>
  Object.prototype.hasOwnProperty.call(SMART_SPRINT_LABELS, k);

export function readSmart(v: unknown): SmartValue | undefined {
  if (!isRecord(v)) return undefined;
  const key = v.smart;
  return typeof key === 'string' && isSmartKey(key) ? { smart: key } : undefined;
}

export function readRelativeAmount(v: unknown): RelativeAmount | undefined {
  if (!isRecord(v)) return undefined;
  const { amount } = v;
  const unit = RELATIVE_UNITS.find((u) => u === v.unit);
  return typeof amount === 'number' && Number.isFinite(amount) && unit ? { amount, unit } : undefined;
}

/** A period without a zone stays zoneless: the server reads that as UTC, so it keeps its meaning. */
export function readPeriod(v: unknown): PeriodValue | undefined {
  if (!isRecord(v)) return undefined;
  const { period, timeZone } = v;
  if (typeof period !== 'string' || !PERIOD_KEYS.has(period)) return undefined;
  const out: PeriodValue = { period: period as PeriodKey };
  if (typeof timeZone === 'string' && timeZone !== '') out.timeZone = timeZone;
  return out;
}

export const isSmartValue = (v: unknown): v is SmartValue => readSmart(v) !== undefined;
export const isRelativeAmount = (v: unknown): v is RelativeAmount => readRelativeAmount(v) !== undefined;
export const isPeriodValue = (v: unknown): v is PeriodValue => readPeriod(v) !== undefined;

/** A plain name, number or date — what the text and number inputs edit. */
export const isScalar = (v: ConditionValue | ListValue | undefined): v is string | number =>
  typeof v === 'string' || typeof v === 'number';

export const smartLabel = (v: SmartValue): string => SMART_SPRINT_LABELS[v.smart];

/** The zone "in period" is evaluated in: the browser's own, captured when the operator is chosen. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}
```

Run: `npx vitest run src/features/issues/valueShapes.test.ts` → PASS.

- [ ] **Step 4: Write the failing tree-model tests**

In `src/JiraUnpacked.Web/src/features/issues/filterTree.test.ts`, extend the import from `./filterTree` with `freshCondition, switchOperator, toWireCondition, cloneCondition` and append:

```ts
describe('object values', () => {
  it('reads each object value from a link in canonical form, and drops shapes its operator cannot use', () => {
    const tree = parseFilterParam(JSON.stringify({
      logic: 'And',
      conditions: [
        { field: 'updatedAt', op: 'InPeriod', value: { timeZone: 'Europe/Warsaw', period: 'thisWeek', extra: 1 } },
        { field: 'updatedAt', op: 'InLast', value: { unit: 'days', amount: 14 } },
        { field: 'sprint', op: 'Eq', value: { smart: 'active' } },
        { field: 'sprint', op: 'In', values: [{ smart: 'current' }, 'Sprint 7', { smart: 'open' }, null] },
        { field: 'createdAt', op: 'Between', values: ['2026-09-01', null] },
        { field: 'updatedAt', op: 'InLast', value: { amount: '14', unit: 'days' } },
        { field: 'updatedAt', op: 'InPeriod', value: { period: 'fortnight' } },
        { field: 'createdAt', op: 'Lt', value: { smart: 'active' } },
        { field: 'createdAt', op: 'Between', values: ['2026-09-01'] },
      ],
    }), null);

    const [period, last, smartEq, smartIn, between, badAmount, badPeriod, badLt, badBetween] = tree.conditions;
    expect(period.value).toEqual({ period: 'thisWeek', timeZone: 'Europe/Warsaw' });
    expect(Object.keys(period.value as object)).toEqual(['period', 'timeZone']);
    expect(last.value).toEqual({ amount: 14, unit: 'days' });
    expect(smartEq.value).toEqual({ smart: 'active' });
    expect(smartIn.values).toEqual(['Sprint 7', { smart: 'open' }, null]);
    expect(between.values).toEqual(['2026-09-01', null]);
    for (const c of [badAmount, badPeriod, badLt, badBetween]) {
      expect(isCompleteCondition(c)).toBe(false);   // kept on screen, never sent
    }
  });

  it('decides completeness per operator', () => {
    const c = (rest: Omit<EditorCondition, 'id'>) => cond('x', rest);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: [null, null] }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: ['', null] }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'createdAt', op: 'Between', values: [null, '2026-09-30'] }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'storyPoints', op: 'Between', values: [0, null] }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 0, unit: 'days' } }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InLast', value: { amount: 1.5, unit: 'days' } }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'NotInLast', value: { amount: 1, unit: 'hours' } }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } }))).toBe(true);
    expect(isCompleteCondition(c({ field: 'updatedAt', op: 'InPeriod' }))).toBe(false);
    expect(isCompleteCondition(c({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }))).toBe(true);
  });

  it('sends an empty Between bound as an open one, and copies object values rather than sharing them', () => {
    expect(toWireCondition({ field: 'createdAt', op: 'Between', values: ['2026-09-01', ''] }))
      .toEqual({ field: 'createdAt', op: 'Between', values: ['2026-09-01', null] });

    const value = { amount: 14, unit: 'days' as const };
    const wire = toWireCondition({ field: 'updatedAt', op: 'InLast', value });
    expect(wire).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(wire.value).not.toBe(value);
  });

  it('round-trips every object value through the URL form', () => {
    const tree = group('r', {
      conditions: [
        cond('a', { field: 'updatedAt', op: 'InLast', value: { amount: 3, unit: 'weeks' } }),
        cond('b', { field: 'updatedAt', op: 'InPeriod', value: { period: 'lastMonth', timeZone: 'Europe/Warsaw' } }),
        cond('c', { field: 'storyPoints', op: 'Between', values: [1.5, null] }),
        cond('d', { field: 'sprint', op: 'In', values: [{ smart: 'open' }, null, 'Sprint 7'] }),
        cond('e', { field: 'sprint', op: 'Neq', value: { smart: 'active' } }),
      ],
    });

    expect(pruneTree(parseFilterParam(toUrlJson(tree), null))).toEqual(pruneTree(tree));
  });

  it('copies object values when a condition is duplicated', () => {
    const original = cond('a', { field: 'updatedAt', op: 'InPeriod', value: { period: 'today', timeZone: 'UTC' } });
    const copy = cloneCondition(original);

    expect(copy.value).toEqual(original.value);
    expect(copy.value).not.toBe(original.value);
  });
});

describe('freshCondition / switchOperator', () => {
  const TZ = 'Europe/Warsaw';

  it('starts the relative operators at a default that already filters, and the rest empty', () => {
    expect(freshCondition('updatedAt', 'InLast', TZ)).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(freshCondition('updatedAt', 'InPeriod', TZ)).toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: TZ } });
    expect(freshCondition('createdAt', 'Between', TZ)).toEqual({ field: 'createdAt', op: 'Between', values: [null, null] });
    expect(freshCondition('status', 'In', TZ)).toEqual({ field: 'status', op: 'In', values: [] });
    expect(freshCondition('status', 'Eq', TZ)).toEqual({ field: 'status', op: 'Eq' });
  });

  it('keeps what still means the same thing, and starts over otherwise', () => {
    expect(switchOperator({ field: 'updatedAt', op: 'InLast', value: { amount: 30, unit: 'weeks' } }, 'NotInLast', TZ))
      .toEqual({ field: 'updatedAt', op: 'NotInLast', value: { amount: 30, unit: 'weeks' } });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'InLast', TZ))
      .toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'InPeriod', TZ))
      .toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: TZ } });
    expect(switchOperator({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } }, 'Lt', TZ))
      .toEqual({ field: 'updatedAt', op: 'Lt', value: '' });
    expect(switchOperator({ field: 'updatedAt', op: 'Lt', value: '2026-01-01' }, 'Gte', TZ))
      .toEqual({ field: 'updatedAt', op: 'Gte', value: '2026-01-01' });
    expect(switchOperator({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }, 'Neq', TZ))
      .toEqual({ field: 'sprint', op: 'Neq', value: { smart: 'active' } });
    expect(switchOperator({ field: 'sprint', op: 'Eq', value: { smart: 'active' } }, 'In', TZ))
      .toEqual({ field: 'sprint', op: 'In', values: [] });
    expect(switchOperator({ field: 'status', op: 'In', values: ['Done'] }, 'NotIn', TZ))
      .toEqual({ field: 'status', op: 'NotIn', values: ['Done'] });
    expect(switchOperator({ field: 'status', op: 'Eq', value: 'Done' }, 'IsNull', TZ))
      .toEqual({ field: 'status', op: 'IsNull' });
  });
});
```

Run: `npx vitest run src/features/issues/filterTree.test.ts`
Expected: FAIL — `freshCondition is not a function` (and the object-value cases fail on dropped values).

- [ ] **Step 5: Implement the model in `filterTree.ts`**

In `src/JiraUnpacked.Web/src/features/issues/filterTree.ts`:

(a) Replace the imports:

```ts
import type {
  ConditionValue, FilterCondition, FilterGroup, FilterLogic, FilterOperator, ListValue,
} from '../../api/issues';
import { OPERATOR_LABELS } from './filterCatalogHelpers';
import {
  DEFAULT_PERIOD, DEFAULT_RELATIVE, isRelativeAmount, isScalar, isSmartValue, readPeriod, readRelativeAmount,
  readSmart,
} from './valueShapes';
```

(b) After `export const takesList = …`, add:

```ts
/** "between": `values: [from, to]`, either bound null (open). */
export const takesRange = (op: FilterOperator): boolean => op === 'Between';
/** "in the last" / "more than … ago": `value: { amount, unit }`. */
export const takesAmount = (op: FilterOperator): boolean => op === 'InLast' || op === 'NotInLast';
/** "in period": `value: { period, timeZone }`. */
export const takesPeriod = (op: FilterOperator): boolean => op === 'InPeriod';

const isBound = (v: ListValue | undefined): boolean => v !== undefined && v !== null && v !== '';
/** An empty bound is an open one: the server reads null as "no limit on this side". */
const asBound = (v: ListValue | undefined): ListValue => (isBound(v) ? (v as ListValue) : null);

/** Object values are copied, never shared: two rows — or the editor and the wire — must not alias one object. */
function copyValue<T>(v: T): T {
  return (typeof v === 'object' && v !== null ? { ...v } : v) as T;
}
```

(c) Replace `isCompleteCondition` and `toWireCondition`:

```ts
/**
 * A condition the user has started but not finished is unfinished, not invalid: it is left out
 * of the request rather than sent to earn a 400 mid-edit. A null `value` is (Empty) and complete.
 * Between needs at least one bound; in the last / more than … ago a whole amount of at least 1;
 * in period a period.
 */
export function isCompleteCondition(c: FilterCondition): boolean {
  if (takesNoValue(c.op)) return true;
  if (takesList(c.op)) return (c.values?.length ?? 0) > 0;
  if (takesRange(c.op)) return (c.values ?? []).some(isBound);
  if (takesAmount(c.op)) {
    const rel = readRelativeAmount(c.value);
    return rel !== undefined && Number.isInteger(rel.amount) && rel.amount >= 1;
  }
  if (takesPeriod(c.op)) return readPeriod(c.value) !== undefined;
  return c.value !== undefined && c.value !== '';
}

/** One condition as the server takes it: only its operator's key, (Empty) on is/is not as is empty/is not empty. */
export function toWireCondition(c: FilterCondition): FilterCondition {
  if ((c.op === 'Eq' || c.op === 'Neq') && c.value === null) {
    return { field: c.field, op: c.op === 'Eq' ? 'IsNull' : 'IsNotNull' };
  }
  if (takesNoValue(c.op)) return { field: c.field, op: c.op };
  if (takesList(c.op)) return { field: c.field, op: c.op, values: (c.values ?? []).map(copyValue) };
  if (takesRange(c.op)) {
    const [from, to] = c.values ?? [];
    return { field: c.field, op: c.op, values: [asBound(from), asBound(to)] };
  }
  return { field: c.field, op: c.op, value: copyValue(c.value) };
}
```

(d) Replace `cloneCondition`:

```ts
export const cloneCondition = (c: EditorCondition): EditorCondition => ({
  ...c,
  id: newId(),
  ...(c.values ? { values: c.values.map(copyValue) } : {}),
  ...(c.value !== undefined ? { value: copyValue(c.value) } : {}),
});
```

(e) After `withIdentityOf`, add:

```ts
/**
 * A row for `op` with nothing chosen yet — except the relative-date operators, which start at a
 * useful default so choosing one already filters: "in the last 14 days", "in period: this week"
 * (in `timeZone`, the browser's, captured now and saved with the condition).
 */
export function freshCondition(field: string, op: FilterOperator, timeZone: string): FilterCondition {
  if (takesList(op)) return { field, op, values: [] };
  if (takesRange(op)) return { field, op, values: [null, null] };
  if (takesAmount(op)) return { field, op, value: { ...DEFAULT_RELATIVE } };
  if (takesPeriod(op)) return { field, op, value: { period: DEFAULT_PERIOD, timeZone } };
  return { field, op };
}

/**
 * The row after its operator changes, rebuilt with only the key the new operator takes. What
 * still means the same thing carries over: the list between is any of / is none of, the amount
 * between in the last / more than … ago, a typed value between the scalar operators, and a smart
 * sprint value between is / is not. Everything else starts over (freshCondition).
 */
export function switchOperator(c: FilterCondition, nextOp: FilterOperator, timeZone: string): FilterCondition {
  const { field, op } = c;
  if (takesList(nextOp)) return { field, op: nextOp, values: takesList(op) ? [...(c.values ?? [])] : [] };
  if (takesAmount(nextOp) && isRelativeAmount(c.value)) return { field, op: nextOp, value: { ...c.value } };
  if (takesNoValue(nextOp) || takesRange(nextOp) || takesAmount(nextOp) || takesPeriod(nextOp)) {
    return freshCondition(field, nextOp, timeZone);
  }
  const keep = isScalar(c.value) || (isSmartValue(c.value) && (nextOp === 'Eq' || nextOp === 'Neq'));
  return { field, op: nextOp, value: keep ? c.value : '' };
}
```

(f) Replace `readCondition` (keep its null-normalisation block and comment exactly as they are) with:

```ts
/**
 * A `value` in the shape its operator takes, as a fresh object in canonical key order; anything
 * else is dropped, leaving the row unfinished — kept on screen, never sent.
 */
function readValue(op: FilterOperator, raw: unknown): ConditionValue | undefined {
  if (takesAmount(op)) return readRelativeAmount(raw);
  if (takesPeriod(op)) return readPeriod(raw);
  if (typeof raw === 'string' || typeof raw === 'number' || raw === null) return raw;
  // Only is / is not take an object as `value`: a smart sprint value.
  return op === 'Eq' || op === 'Neq' ? readSmart(raw) : undefined;
}

/** `values` as its operator takes them: exactly [from, to] for Between; else names, numbers, (Empty) and smart values. */
function readValues(op: FilterOperator, raw: unknown): ListValue[] | undefined {
  if (!Array.isArray(raw)) return undefined;
  if (takesRange(op)) {
    return raw.length === 2
      ? raw.map((x): ListValue => (typeof x === 'string' || typeof x === 'number' ? x : null))
      : undefined;
  }
  const out: ListValue[] = [];
  for (const x of raw) {
    if (typeof x === 'string' || typeof x === 'number' || x === null) out.push(x);
    else {
      const smart = readSmart(x);
      if (smart) out.push(smart);
    }
  }
  return out;
}

function readCondition(v: unknown): EditorCondition | null {
  if (!isRecord(v) || typeof v.field !== 'string' || typeof v.op !== 'string') return null;
  if (!isKnownOperator(v.op)) return null;
  const c: EditorCondition = { id: newId(), field: v.field, op: v.op as FilterOperator };
  const value = readValue(c.op, v.value);
  if (value !== undefined) c.value = value;
  const values = readValues(c.op, v.values);
  if (values !== undefined) c.values = values;
  if (v.disabled === true) c.disabled = true;

  // …the existing comment and `if (c.value === null) { … }` block, unchanged…
  return c;
}
```

(the `// …` line stands for the existing block that starts `// A hand-edited (or hand-crafted) link can carry \`value: null\`` and ends with `delete c.value;\n  }` — move it over verbatim).

Run: `npx vitest run src/features/issues/filterTree.test.ts` → PASS (existing cases included).

- [ ] **Step 6: Labels and the dictionary type**

In `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.ts`, replace `isDictionaryField` and add the four labels at the end of `OPERATOR_LABELS`:

```ts
/** Fields whose values come from this project's value list: dictionaries, and status category. */
export const isDictionaryField = (catalog: FilterCatalog, field: string): boolean => {
  const type = typeFor(catalog, field);
  return type === 'DictName' || type === 'StatusCategory';
};
```

```ts
  StartsWith: 'starts with',
  Between: 'between',
  InLast: 'in the last',
  NotInLast: 'more than … ago',
  InPeriod: 'in period',
};
```

In `src/JiraUnpacked.Web/src/features/issues/filterCatalogHelpers.test.ts`, append:

```ts
describe('richer values', () => {
  it('names the range and relative operators in words', () => {
    expect((['Between', 'InLast', 'NotInLast', 'InPeriod'] as const).map(operatorLabel))
      .toEqual(['between', 'in the last', 'more than … ago', 'in period']);
  });

  it('gives status category the value picker, like a dictionary field', () => {
    const catalog: FilterCatalog = {
      fields: [{ name: 'statusCategory', type: 'StatusCategory', operators: ['Eq', 'In'] }],
      maxConditions: 20, maxInValues: 100, maxGroups: 10, maxDepth: 3,
    };
    expect(isDictionaryField(catalog, 'statusCategory')).toBe(true);
  });
});
```

- [ ] **Step 7: The catalog fixture and typed item fixtures**

Replace the body of `src/JiraUnpacked.Web/src/test/filterCatalogFixture.ts` below the imports with:

```ts
/**
 * GET /api/filter-catalog as the server serves it after richer filter values: every field's
 * operators in FilterCatalogService.DisplayOrder. Field order here is the tests' own (status
 * first); the server sorts fields by name, which nothing in the editor depends on.
 */
const DICT = ['Eq', 'Neq', 'In', 'NotIn', 'IsNull', 'IsNotNull'] as const;
const DATE = ['Lt', 'Lte', 'Gt', 'Gte', 'Between', 'InLast', 'NotInLast', 'InPeriod'] as const;

export const FILTER_CATALOG: FilterCatalog = {
  fields: [
    { name: 'status', type: 'DictName', operators: [...DICT] },
    { name: 'statusCategory', type: 'StatusCategory', operators: [...DICT] },
    { name: 'issueType', type: 'DictName', operators: [...DICT] },
    { name: 'priority', type: 'DictName', operators: [...DICT] },
    { name: 'assignee', type: 'DictName', operators: [...DICT] },
    { name: 'sprint', type: 'DictName', operators: [...DICT] },
    { name: 'team', type: 'DictName', operators: [...DICT] },
    { name: 'storyPoints', type: 'Decimal', operators: ['Eq', 'Neq', 'Lt', 'Lte', 'Gt', 'Gte', 'Between', 'IsNull', 'IsNotNull'] },
    { name: 'createdAt', type: 'DateTime', operators: [...DATE] },
    { name: 'updatedAt', type: 'DateTime', operators: [...DATE] },
    { name: 'summary', type: 'Text', operators: ['Contains', 'NotContains', 'StartsWith', 'IsNull', 'IsNotNull'] },
    { name: 'jiraKey', type: 'Text', operators: ['Eq', 'In', 'NotIn', 'Contains', 'StartsWith'] },
    { name: 'parent', type: 'ParentRef', operators: ['Eq', 'IsNull', 'IsNotNull'] },
  ],
  maxConditions: 20,
  maxInValues: 100,
  maxGroups: 10,
  maxDepth: 3,
};
```

In `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`, in `row()`, add `statusCategory: 'To Do',` after `status: 'Open',`. In `src/JiraUnpacked.Web/src/features/issues/IssueDetail.test.tsx`, in `detail()`'s `issue`, add `statusCategory: 'Done',` after `status: 'Done',`.

- [ ] **Step 8: Smart values as chips in `MultiValuePicker`**

In `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx`, change the type import to `import type { FilterValueOption, ListValue, SmartValue } from '../../api/issues';`, add `import { smartLabel } from './valueShapes';`, and replace everything from `interface MultiValuePickerProps` to the end of the file with:

```tsx
interface MultiValuePickerProps {
  /** Accessible name of the whole control, e.g. "Values for condition 1". */
  label: string;
  options: FilterValueOption[];
  emptyCount: number;
  /** null = (Empty); { smart } = a smart sprint value. */
  selected: ListValue[];
  onChange: (next: ListValue[]) => void;
  max: number;
}

type Choice = string | null | SmartValue;

/** What a value MEANS, as a key: two equal smart objects are one choice, whatever their identity. */
const keyOf = (v: Choice) => (v === null ? 'empty' : typeof v === 'string' ? `v:${v}` : `s:${v.smart}`);
const labelOf = (v: Choice) => (v === null ? EMPTY_LABEL : typeof v === 'string' ? v : smartLabel(v));

/**
 * "is any of" / "is none of" on a dictionary field: chips for what is chosen, and a popover with
 * a search box and a checkbox per value — (Empty) first — each with its count. A checkbox list
 * makes choosing the same value twice impossible, which free text never could.
 *
 * A native <details> for the popover, like ColumnChooser: it opens, closes and takes focus with
 * no JavaScript and no outside-click handling to get wrong.
 */
export function MultiValuePicker({ label, options, emptyCount, selected, onChange, max }: MultiValuePickerProps) {
  const [query, setQuery] = useState('');
  const detailsRef = useRef<HTMLDetailsElement>(null);
  const summaryRef = useRef<HTMLElement>(null);

  // <details> opens and closes natively, but does not close on Escape the way any other popover
  // does — without this, focus would be trapped inside an open list with no keyboard way out.
  const handleKeyDown = (event: KeyboardEvent<HTMLDetailsElement>) => {
    if (event.key !== 'Escape' || !detailsRef.current?.open) return;
    event.preventDefault();
    detailsRef.current.open = false;
    summaryRef.current?.focus();
  };

  // De-duplicated on the way in, by meaning, so even a hand-edited link cannot show a value twice.
  const chosen: Choice[] = [];
  const chosenKeys = new Set<string>();
  for (const raw of selected) {
    const v: Choice = typeof raw === 'number' ? String(raw) : raw;
    if (!chosenKeys.has(keyOf(v))) {
      chosenKeys.add(keyOf(v));
      chosen.push(v);
    }
  }
  const isChosen = (v: Choice) => chosenKeys.has(keyOf(v));
  const atLimit = chosen.length >= max;
  const toggle = (v: Choice) =>
    onChange(isChosen(v) ? chosen.filter((x) => keyOf(x) !== keyOf(v)) : [...chosen, v]);

  const needle = query.trim().toLowerCase();
  const matches = options.filter((o) => o.name.toLowerCase().includes(needle));
  const showEmpty = needle === '' || EMPTY_LABEL.toLowerCase().includes(needle);

  const choice = (v: Choice, text: string, count: number) => (
    <label key={keyOf(v)} className="flex items-center gap-2 text-sm text-[var(--ju-text)]">
      <input type="checkbox" checked={isChosen(v)} disabled={!isChosen(v) && atLimit} onChange={() => toggle(v)} />
      <span className="flex-1">{text}</span>{' '}
      <span className="text-xs text-[var(--ju-muted)]">{count}</span>
    </label>
  );

  return (
    <div role="group" aria-label={label} className="flex flex-col gap-1">
      <span aria-hidden="true" className="text-sm font-medium text-[var(--ju-text)]">{label}</span>
      {chosen.length > 0 && (
        <div className="flex flex-wrap gap-1">
          {chosen.map((v) => (
            <Badge key={keyOf(v)}>
              {labelOf(v)}
              <button type="button" className="ml-1" aria-label={`Remove ${labelOf(v)}`} onClick={() => toggle(v)}>
                ×
              </button>
            </Badge>
          ))}
        </div>
      )}
      <details ref={detailsRef} className="relative" onKeyDown={handleKeyDown}>
        <summary
          ref={summaryRef}
          className="cursor-pointer list-none rounded-[var(--ju-radius)] border border-[var(--ju-border)] px-3 py-2 text-sm text-[var(--ju-text)]"
        >
          Choose values
        </summary>
        <div className="absolute z-10 mt-1 flex max-h-72 w-64 flex-col gap-2 overflow-auto rounded-[var(--ju-radius)] border border-[var(--ju-border)] bg-[var(--ju-surface)] p-3 shadow-lg">
          <input
            type="search"
            aria-label={`Search ${label}`}
            className={INPUT_CLASS}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          {showEmpty && choice(null, EMPTY_LABEL, emptyCount)}
          {matches.map((o) => choice(o.name, o.name, o.count))}
          {!showEmpty && matches.length === 0 && (
            <p className="text-xs text-[var(--ju-muted)]">No matching values</p>
          )}
        </div>
      </details>
      {atLimit && <p className="text-xs text-[var(--ju-muted)]">You can add at most {max} values.</p>}
    </div>
  );
}
```

In `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`, change `renderMulti`'s signature and state to `ListValue` (add `import type { ListValue } from '../../api/issues';`):

```tsx
function renderMulti(initial: ListValue[] = [], max = 100) {
  let latest = initial;
  function Harness() {
    const [selected, setSelected] = useState<ListValue[]>(initial);
```

and add to `describe('MultiValuePicker', …)`:

```tsx
  it('shows a smart value from a link as a chip named for what it means, and removes it', async () => {
    const user = userEvent.setup();
    const picker = renderMulti([{ smart: 'open' }, 'Backlog', { smart: 'open' }]);

    expect(screen.getByText('Open sprints (active + future)')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /^Remove Open sprints/ })).toHaveLength(1);

    await user.click(screen.getByRole('button', { name: 'Remove Open sprints (active + future)' }));

    expect(picker.latest()).toEqual(['Backlog']);
  });
```

- [ ] **Step 9: Adapt `ConditionRow` to the widened model**

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`:

(a) Replace the `./filterTree` import with `import { freshCondition, switchOperator, takesList, takesNoValue } from './filterTree';` and add `import { browserTimeZone, isScalar, isSmartValue } from './valueShapes';`.

(b) Replace `handleFieldChange` and `handleOperatorChange`:

```tsx
  const handleFieldChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const nextField = event.target.value;
    const nextOp = resetOperatorIfDisallowed(catalog, nextField, op);
    // A value valid for the old field rarely is for the new one, so it is dropped, not carried.
    onChange(freshCondition(nextField, nextOp, browserTimeZone()));
  };

  // What carries over between operators, and what each one starts with, lives in switchOperator.
  const handleOperatorChange = (event: ChangeEvent<HTMLSelectElement>) =>
    onChange(switchOperator(condition, event.target.value as FilterOperator, browserTimeZone()));
```

(c) Replace the fallback `ChipInput` block inside `if (takesList(op))` (from `const isKey = field === 'jiraKey';` to the end of its `return (…);`) with:

```tsx
      const isKey = field === 'jiraKey';
      // Typing cannot produce (Empty) or a smart value, but one that arrived in a link is kept,
      // not silently lost.
      const kept = values.filter((v) => v === null || isSmartValue(v));
      return (
        <ChipInput
          key={field}
          id={valueId}
          label={valueLabel}
          values={values.filter(isScalar).map(String)}
          max={catalog.maxInValues - kept.length}
          splitOn={isKey ? JIRA_KEY_SEPARATORS : undefined}
          normalize={isKey ? toUpper : undefined}
          onChange={(next) => onChange({ field, op, values: [...kept, ...next] })}
        />
      );
```

(d) In the `Decimal` input, replace `value={condition.value ?? ''}` with `value={isScalar(condition.value) ? condition.value : ''}`.

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`, add:

```tsx
  it('keeps a smart value that arrived in a link when the fallback is used', async () => {
    failValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [{ smart: 'active' }] });

    await user.type(await screen.findByLabelText('Value for condition 1'), 'S1{Enter}');

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [{ smart: 'active' }, 'S1'] });
  });
```

- [ ] **Step 10: A stored view with object values is not "modified"**

In `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`, add inside the `isModified` describe block (or at the end of the file, in its own `describe('isModified with object values', …)`):

```ts
  it('does not call a view with relative and smart values modified when the server returns them in its own key order', () => {
    const stored = view({
      filter: {
        logic: 'And',
        conditions: [
          { field: 'updatedAt', op: 'InPeriod', value: { timeZone: 'Europe/Warsaw', period: 'thisWeek' }, values: null, disabled: null },
          { field: 'sprint', op: 'In', value: null, values: [null, { smart: 'open' }], disabled: null },
        ],
        groups: null,
        disabled: null,
      } as unknown as SavedView['filter'],
    });
    // The same list as the editor builds it: objects in the editor's own key order.
    const built: ListSnapshot = {
      tree: {
        id: 'r', logic: 'And', groups: [],
        conditions: [
          { id: 'a', field: 'updatedAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: 'Europe/Warsaw' } },
          { id: 'b', field: 'sprint', op: 'In', values: [null, { smart: 'open' }] },
        ],
      },
      sortBy: 'updatedAt',
      sortDescending: true,
      columns: DEFAULT_COLUMNS,
    };

    expect(isModified(stored, built)).toBe(false);
  });
```

- [ ] **Step 11: Run the web checks**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test`
Expected: PASS — typecheck clean (every consumer of `value`/`values` narrows before use), all suites green.

- [ ] **Step 12: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src
git commit -m "feat(web): object filter values in the model — canonical parse, completeness, operator switching, smart chips" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Value controls — Between, in the last / more than … ago, in period

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`

**Interfaces:**
- Consumes: `takesRange`, `takesAmount`, `takesPeriod` (filterTree, Task 5); `DEFAULT_RELATIVE`, `PERIODS`, `RELATIVE_UNITS`, `browserTimeZone`, `isRelativeAmount`, `isScalar`, `readPeriod` (valueShapes, Task 5); `Field`'s `hint` prop (`src/ui/Field.tsx`, existing — renders muted text under the control and wires `aria-describedby`).
- Produces (accessible names, used by later tests): `From for condition N` / `To for condition N` (Between), `Amount for condition N` / `Unit for condition N` (InLast/NotInLast), `Period for condition N` (InPeriod).

- [ ] **Step 1: Write the failing tests**

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`, change the imports to add `fireEvent` from `@testing-library/react` and `afterEach, vi` from `vitest`, then append:

```tsx
describe('ConditionRow — ranges and relative dates', () => {
  afterEach(() => vi.restoreAllMocks());

  const inZone = (timeZone: string) => {
    const real = Intl.DateTimeFormat().resolvedOptions();
    vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockReturnValue({ ...real, timeZone });
  };

  it('names the date operators in words, in the catalog’s order', () => {
    renderRow({ field: 'createdAt', op: 'Lt', value: '' });

    const ops = within(screen.getByLabelText('Operator for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(ops).toEqual(['<', '≤', '>', '≥', 'between', 'in the last', 'more than … ago', 'in period']);
  });

  it('takes a date range with either end left open', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'createdAt', op: 'Lt', value: '2026-01-01' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Between');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: [null, null] });

    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '2026-09-01' } });
    fireEvent.change(screen.getByLabelText('To for condition 1'), { target: { value: '2026-09-30' } });
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: ['2026-09-01', '2026-09-30'] });

    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '' } });
    expect(row.last()).toEqual({ field: 'createdAt', op: 'Between', values: [null, '2026-09-30'] });
    expect(screen.getByLabelText('To for condition 1')).toHaveAttribute('type', 'date');
  });

  it('takes a story-point range as numbers', () => {
    const row = renderRow({ field: 'storyPoints', op: 'Between', values: [null, null] });

    expect(screen.getByLabelText('From for condition 1')).toHaveAttribute('type', 'number');
    fireEvent.change(screen.getByLabelText('From for condition 1'), { target: { value: '1.5' } });
    fireEvent.change(screen.getByLabelText('To for condition 1'), { target: { value: '8' } });

    expect(row.last()).toEqual({ field: 'storyPoints', op: 'Between', values: [1.5, 8] });
  });

  it('starts "in the last" at 14 days and edits the amount and the unit', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'Lt', value: '' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'InLast');
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } });
    expect(screen.getByLabelText('Amount for condition 1')).toHaveValue(14);

    fireEvent.change(screen.getByLabelText('Amount for condition 1'), { target: { value: '30' } });
    await user.selectOptions(screen.getByLabelText('Unit for condition 1'), 'weeks');
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 30, unit: 'weeks' } });

    fireEvent.change(screen.getByLabelText('Amount for condition 1'), { target: { value: '' } });
    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InLast', value: { amount: 0, unit: 'weeks' } });
    expect(screen.getByLabelText('Amount for condition 1')).toHaveValue(null);
  });

  it('reads "more than [14] [days] ago" and keeps the amount when flipped', async () => {
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'InLast', value: { amount: 3, unit: 'months' } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'NotInLast');

    expect(row.last()).toEqual({ field: 'updatedAt', op: 'NotInLast', value: { amount: 3, unit: 'months' } });
    expect(screen.getByText('ago')).toBeInTheDocument();
    expect(within(screen.getByLabelText('Unit for condition 1')).getAllByRole('option').map((o) => o.textContent))
      .toEqual(['hours', 'days', 'weeks', 'months']);
  });

  it('captures the browser’s zone with "in period", shows it, and keeps it when the period changes', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const row = renderRow({ field: 'createdAt', op: 'Lt', value: '' });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'InPeriod');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'InPeriod', value: { period: 'thisWeek', timeZone: 'Europe/Warsaw' } });
    expect(screen.getByText('Europe/Warsaw')).toBeInTheDocument();

    const periods = within(screen.getByLabelText('Period for condition 1')).getAllByRole('option').map((o) => o.textContent);
    expect(periods).toEqual([
      'Today', 'Yesterday', 'This week', 'Last week', 'This month', 'Last month',
      'This quarter', 'Last quarter', 'This year', 'Last year',
    ]);

    inZone('America/New_York');   // the zone was captured when the operator was chosen, not now
    await user.selectOptions(screen.getByLabelText('Period for condition 1'), 'lastMonth');
    expect(row.last()).toEqual({ field: 'createdAt', op: 'InPeriod', value: { period: 'lastMonth', timeZone: 'Europe/Warsaw' } });
  });

  it('keeps a zoneless period from a link zoneless — it means UTC and says so', async () => {
    inZone('Europe/Warsaw');
    const user = userEvent.setup();
    const row = renderRow({ field: 'updatedAt', op: 'InPeriod', value: { period: 'today' } });

    expect(screen.getByText('UTC')).toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Period for condition 1'), 'yesterday');

    expect(row.last()).toEqual({ field: 'updatedAt', op: 'InPeriod', value: { period: 'yesterday' } });
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ConditionRow.test.tsx`
Expected: the new tests FAIL (`Unable to find a label with the text of: From for condition 1`, and so on); the date-operator label test passes already (Task 5).

- [ ] **Step 2: Implement the controls**

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`:

(a) Imports become:

```tsx
import type { ChangeEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import type {
  FilterCatalog, FilterCondition, FilterOperator, ListValue, PeriodKey, PeriodValue, RelativeAmount, RelativeUnit,
} from '../../api/issues';
import { getFilterValues } from '../../api/issues';
import { queryKeys } from '../../api/queryKeys';
import { Field, Select } from '../../ui';
import { ChipInput } from './ChipInput';
import {
  fieldNames, isDictionaryField, operatorLabel, operatorsFor, resetOperatorIfDisallowed, typeFor,
} from './filterCatalogHelpers';
import {
  freshCondition, switchOperator, takesAmount, takesList, takesNoValue, takesPeriod, takesRange,
} from './filterTree';
import { INPUT_CLASS } from './styles';
import {
  DEFAULT_RELATIVE, PERIODS, RELATIVE_UNITS, browserTimeZone, isRelativeAmount, isScalar, isSmartValue, readPeriod,
} from './valueShapes';
import { MultiValuePicker, SingleValueSelect } from './ValuePicker';
```

(b) In `renderValueControl`, directly after `if (takesNoValue(op)) return null;`, insert:

```tsx
    if (takesRange(op)) {
      // Two bounds, either left blank for an open end. Dates for the date fields, numbers for points.
      const [from = null, to = null] = condition.values ?? [];
      const numeric = fieldType === 'Decimal';
      const shown = (v: ListValue) => (isScalar(v) ? String(v) : '');
      const setBound = (index: 0 | 1, raw: string) => {
        const bound: ListValue = raw === '' ? null : numeric ? Number(raw) : raw;
        onChange({ field, op, values: index === 0 ? [bound, to] : [from, bound] });
      };
      return (
        <>
          <Field label={`From for condition ${label}`} id={`condition-${label}-from`}>
            <input
              type={numeric ? 'number' : 'date'}
              step={numeric ? 'any' : undefined}
              className={INPUT_CLASS}
              value={shown(from)}
              onChange={(e) => setBound(0, e.target.value)}
            />
          </Field>
          <Field label={`To for condition ${label}`} id={`condition-${label}-to`}>
            <input
              type={numeric ? 'number' : 'date'}
              step={numeric ? 'any' : undefined}
              className={INPUT_CLASS}
              value={shown(to)}
              onChange={(e) => setBound(1, e.target.value)}
            />
          </Field>
        </>
      );
    }

    if (takesAmount(op)) {
      // An amount of 0 is "not entered yet": the input shows blank and the row is unfinished.
      const rel: RelativeAmount = isRelativeAmount(condition.value)
        ? condition.value
        : { amount: 0, unit: DEFAULT_RELATIVE.unit };
      const set = (next: RelativeAmount) => onChange({ field, op, value: next });
      return (
        <>
          <Field label={`Amount for condition ${label}`} id={`condition-${label}-amount`}>
            <input
              type="number"
              min={1}
              step={1}
              className={INPUT_CLASS}
              value={rel.amount >= 1 ? rel.amount : ''}
              onChange={(e) => set({
                amount: e.target.value === '' ? 0 : Math.trunc(Number(e.target.value)),
                unit: rel.unit,
              })}
            />
          </Field>
          <Field label={`Unit for condition ${label}`} id={`condition-${label}-unit`}>
            <Select value={rel.unit} onChange={(e) => set({ amount: rel.amount, unit: e.target.value as RelativeUnit })}>
              {RELATIVE_UNITS.map((u) => (
                <option key={u} value={u}>{u}</option>
              ))}
            </Select>
          </Field>
          {/* "more than [14] [days] ago": the operator reads "more than … ago", the row ends the sentence. */}
          {op === 'NotInLast' && <span className="pb-2 text-sm text-[var(--ju-text)]">ago</span>}
        </>
      );
    }

    if (takesPeriod(op)) {
      const current = readPeriod(condition.value);
      const pick = (period: PeriodKey) => {
        const next: PeriodValue = { period };
        // The zone captured with the operator stays with the condition. One that never had a zone
        // (a link from elsewhere) means UTC on the server, and keeps meaning it.
        const zone = current ? current.timeZone : browserTimeZone();
        if (zone) next.timeZone = zone;
        onChange({ field, op, value: next });
      };
      return (
        <Field label={`Period for condition ${label}`} id={`condition-${label}-period`} hint={current?.timeZone ?? 'UTC'}>
          <Select value={current?.period ?? ''} onChange={(e) => pick(e.target.value as PeriodKey)}>
            {!current && <option value="">— choose —</option>}
            {PERIODS.map((p) => (
              <option key={p.key} value={p.key}>{p.label}</option>
            ))}
          </Select>
        </Field>
      );
    }
```

- [ ] **Step 3: Run the tests**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ConditionRow.test.tsx src/features/issues/FilterBuilder.test.tsx src/features/issues/GroupEditor.test.tsx && npm run typecheck && npm run lint`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx
git commit -m "feat(web): value controls for between, in the last / more than … ago, and in period" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: The Smart section in both sprint pickers

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx` (two JSX blocks)
- Modify: `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`

**Interfaces:**
- Consumes: `SmartValueOption`, `FilterValues.smart` (Task 5 types, Task 3 server); `SMART_SPRINT_LABELS`, `isSmartKey`, `smartLabel`, `isSmartValue` (valueShapes).
- Produces: `SingleValueSelect` gains `smart?: SmartValueOption[]`, `smartValue?: SmartSprintKey`, `onPickSmart?: (key: SmartSprintKey) => void`; `MultiValuePicker` gains `smart?: SmartValueOption[]`. Both render the Smart entries after (Empty) and before the names; an unknown key from the server is skipped.

- [ ] **Step 1: Write the failing tests**

In `src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx`, add a smart fixture after `OPTIONS` and extend `renderMulti` to pass it through:

```tsx
const SMART = [
  { key: 'active' as const, label: 'Active sprint', count: 1 },
  { key: 'future' as const, label: 'Future sprints', count: 0 },
  { key: 'open' as const, label: 'Open sprints (active + future)', count: 1 },
  { key: 'closed' as const, label: 'Closed sprints', count: 4 },
];

function renderMulti(initial: ListValue[] = [], max = 100, smart?: typeof SMART) {
  let latest = initial;
  function Harness() {
    const [selected, setSelected] = useState<ListValue[]>(initial);
    return (
      <MultiValuePicker
        label="Values for condition 1"
        options={OPTIONS}
        emptyCount={7}
        smart={smart}
        selected={selected}
        max={max}
        onChange={(next) => { latest = next; setSelected(next); }}
      />
    );
  }
  render(<Harness />);
  return { latest: () => latest };
}
```

and append:

```tsx
describe('Smart section', () => {
  it('lists the smart values after (Empty) and before the names, and picks one', async () => {
    const user = userEvent.setup();
    const picker = renderMulti([], 100, SMART);
    await user.click(screen.getByText('Choose values'));

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual([
      '(Empty) 7', 'Active sprint 1', 'Future sprints 0', 'Open sprints (active + future) 1', 'Closed sprints 4',
      'Backlog 42', 'In Progress 5',
    ]);
    expect(within(screen.getByRole('group', { name: 'Smart' })).getAllByRole('checkbox')).toHaveLength(4);

    await user.click(screen.getByRole('checkbox', { name: /^open sprints/i }));
    await user.click(screen.getByRole('checkbox', { name: /^backlog/i }));

    expect(picker.latest()).toEqual([{ smart: 'open' }, 'Backlog']);
  });

  it('searches the smart values too', async () => {
    const user = userEvent.setup();
    renderMulti([], 100, SMART);
    await user.click(screen.getByText('Choose values'));

    await user.type(screen.getByRole('searchbox'), 'clos');

    expect(screen.getAllByRole('checkbox').map((b) => b.closest('label')?.textContent)).toEqual(['Closed sprints 4']);
  });

  it('offers the smart values in the single select, and shows the chosen one', async () => {
    const onPickSmart = vi.fn();
    const { rerender } = render(
      <SingleValueSelect aria-label="Value" value="" options={OPTIONS} emptyCount={7} smart={SMART}
        onPick={vi.fn()} onPickEmpty={vi.fn()} onPickSmart={onPickSmart} />,
    );

    expect(within(screen.getByLabelText('Value')).getAllByRole('option').map((o) => o.textContent)).toEqual([
      '— choose —', '(Empty) (7)', 'Active sprint (1)', 'Future sprints (0)', 'Open sprints (active + future) (1)',
      'Closed sprints (4)', 'Backlog (42)', 'In Progress (5)',
    ]);

    await userEvent.setup().selectOptions(screen.getByLabelText('Value'), 'Active sprint (1)');
    expect(onPickSmart).toHaveBeenCalledWith('active');

    rerender(
      <SingleValueSelect aria-label="Value" value="" options={OPTIONS} emptyCount={7} smart={SMART} smartValue="closed"
        onPick={vi.fn()} onPickEmpty={vi.fn()} onPickSmart={onPickSmart} />,
    );
    expect(screen.getByLabelText('Value')).toHaveDisplayValue('Closed sprints (4)');
  });
});
```

(`within` joins the `@testing-library/react` import; `vi` is already imported.)

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx`, add a sprint value mock and two tests:

```tsx
function mockSprintValues() {
  server.use(http.get('/api/projects/p1/filter-values', () => HttpResponse.json({
    field: 'sprint',
    values: [{ name: 'Sprint 7', count: 3 }],
    emptyCount: 2,
    smart: [
      { key: 'active', label: 'Active sprint', count: 3 },
      { key: 'future', label: 'Future sprints', count: 0 },
      { key: 'open', label: 'Open sprints (active + future)', count: 3 },
      { key: 'closed', label: 'Closed sprints', count: 9 },
    ],
  })));
}

describe('ConditionRow — smart sprint values', () => {
  it('picks a smart value for "is", and keeps it when flipped to "is not"', async () => {
    mockSprintValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'Eq', value: '' });
    await waitForPicker();

    await user.selectOptions(screen.getByLabelText('Value for condition 1'), 'Active sprint (3)');
    expect(row.last()).toEqual({ field: 'sprint', op: 'Eq', value: { smart: 'active' } });

    await user.selectOptions(screen.getByLabelText('Operator for condition 1'), 'Neq');
    expect(row.last()).toEqual({ field: 'sprint', op: 'Neq', value: { smart: 'active' } });
    expect(screen.getByLabelText('Value for condition 1')).toHaveDisplayValue('Active sprint (3)');
  });

  it('picks smart values and names together for "is any of"', async () => {
    mockSprintValues();
    const user = userEvent.setup();
    const row = renderRow({ field: 'sprint', op: 'In', values: [] });

    await user.click(await screen.findByText('Choose values'));
    await user.click(await screen.findByRole('checkbox', { name: /^open sprints/i }));
    await user.click(screen.getByRole('checkbox', { name: /^sprint 7/i }));

    expect(row.last()).toEqual({ field: 'sprint', op: 'In', values: [{ smart: 'open' }, 'Sprint 7'] });
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ValuePicker.test.tsx src/features/issues/ConditionRow.test.tsx`
Expected: FAIL — no `Smart` group, no `Active sprint` options; typecheck of the test files fails on the unknown `smart` prop.

- [ ] **Step 2: Implement the Smart section**

In `src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx`, change the imports to:

```tsx
import { useRef, useState, type KeyboardEvent, type SelectHTMLAttributes } from 'react';
import type { FilterValueOption, ListValue, SmartSprintKey, SmartValue, SmartValueOption } from '../../api/issues';
import { Badge, Select } from '../../ui';
import { INPUT_CLASS } from './styles';
import { SMART_SPRINT_LABELS, isSmartKey, smartLabel } from './valueShapes';
```

add after `const EMPTY_VALUE = 'e:';`:

```tsx
const SMART_PREFIX = 's:';

/** The smart values this SPA knows how to name; a key from a newer server is skipped, not shown raw. */
const knownSmart = (smart: SmartValueOption[] | undefined) => (smart ?? []).filter((s) => isSmartKey(s.key));
```

replace `SingleValueSelectProps` and `SingleValueSelect` with:

```tsx
type SingleValueSelectProps = Omit<SelectHTMLAttributes<HTMLSelectElement>, 'value' | 'onChange'> & {
  /** The chosen name; '' = nothing chosen yet, or a smart value is chosen (smartValue). */
  value: string;
  options: FilterValueOption[];
  emptyCount: number;
  onPick: (name: string) => void;
  /** (Empty) is not a value: the caller turns "is (Empty)" into "is empty". */
  onPickEmpty: () => void;
  /** Sprint only: the smart values, offered after (Empty) and before the names. */
  smart?: SmartValueOption[];
  /** The chosen smart value, when that is what is chosen. */
  smartValue?: SmartSprintKey;
  onPickSmart?: (key: SmartSprintKey) => void;
};

/** "is" / "is not" on a dictionary field: one value, (Empty) first, every entry with its count. */
export function SingleValueSelect({
  value, options, emptyCount, onPick, onPickEmpty, smart, smartValue, onPickSmart, ...rest
}: SingleValueSelectProps) {
  const offered = knownSmart(smart);
  // A value from a link that this project no longer has is still shown as what it is, not
  // replaced on screen by "— choose —" while the filter silently keeps it.
  const stale = value !== '' && !options.some((o) => o.name === value);
  // Likewise a smart value from a link that this server did not offer.
  const staleSmart = smartValue !== undefined && !offered.some((s) => s.key === smartValue) ? smartValue : undefined;
  const selected = smartValue ? SMART_PREFIX + smartValue : value === '' ? '' : VALUE_PREFIX + value;
  return (
    <Select
      {...rest}
      value={selected}
      onChange={(e) => {
        const v = e.target.value;
        if (v === EMPTY_VALUE) onPickEmpty();
        else if (v.startsWith(SMART_PREFIX)) {
          const key = v.slice(SMART_PREFIX.length);
          if (isSmartKey(key)) onPickSmart?.(key);
        } else if (v.startsWith(VALUE_PREFIX)) onPick(v.slice(VALUE_PREFIX.length));
      }}
    >
      <option value="">— choose —</option>
      <option value={EMPTY_VALUE}>{`${EMPTY_LABEL} (${emptyCount})`}</option>
      {(offered.length > 0 || staleSmart) && (
        <optgroup label="Smart">
          {offered.map((s) => (
            <option key={s.key} value={SMART_PREFIX + s.key}>{`${SMART_SPRINT_LABELS[s.key]} (${s.count})`}</option>
          ))}
          {staleSmart && <option value={SMART_PREFIX + staleSmart}>{SMART_SPRINT_LABELS[staleSmart]}</option>}
        </optgroup>
      )}
      {stale && <option value={VALUE_PREFIX + value}>{`${value} (0)`}</option>}
      {options.map((o) => (
        <option key={o.name} value={VALUE_PREFIX + o.name}>{`${o.name} (${o.count})`}</option>
      ))}
    </Select>
  );
}
```

In `MultiValuePicker`: add `smart?: SmartValueOption[];` to `MultiValuePickerProps` (with the comment `/** Sprint only: the smart values, offered after (Empty) and before the names. */`), add `smart` to the destructured props, and replace the block from `const needle = …` through the closing `</details>` popover contents so it reads:

```tsx
  const needle = query.trim().toLowerCase();
  const matches = options.filter((o) => o.name.toLowerCase().includes(needle));
  const showEmpty = needle === '' || EMPTY_LABEL.toLowerCase().includes(needle);
  const smartMatches = knownSmart(smart).filter((s) => SMART_SPRINT_LABELS[s.key].toLowerCase().includes(needle));
```

and inside the popover `<div className="absolute z-10 …">`, replace the three lines after the search input with:

```tsx
          {showEmpty && choice(null, EMPTY_LABEL, emptyCount)}
          {smartMatches.length > 0 && (
            <div role="group" aria-label="Smart" className="flex flex-col gap-1 border-b border-[var(--ju-border)] pb-2">
              <span aria-hidden="true" className="text-xs font-medium text-[var(--ju-muted)]">Smart</span>
              {smartMatches.map((s) => choice({ smart: s.key }, SMART_SPRINT_LABELS[s.key], s.count))}
            </div>
          )}
          {matches.map((o) => choice(o.name, o.name, o.count))}
          {!showEmpty && smartMatches.length === 0 && matches.length === 0 && (
            <p className="text-xs text-[var(--ju-muted)]">No matching values</p>
          )}
```

(`SmartValue` and `ListValue` stay in use through `Choice` and the props; `smartLabel` through `labelOf`.)

- [ ] **Step 3: Wire the pickers in `ConditionRow`**

In `src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx`, the `MultiValuePicker` element gains `smart={picked.smart}`:

```tsx
          <MultiValuePicker
            label={`Values for condition ${label}`}
            options={picked.values}
            emptyCount={picked.emptyCount}
            smart={picked.smart}
            selected={values}
            max={catalog.maxInValues}
            onChange={(next) => onChange({ field, op, values: next })}
          />
```

and the `SingleValueSelect` element becomes:

```tsx
          <SingleValueSelect
            value={textValue}
            options={picked.values}
            emptyCount={picked.emptyCount}
            smart={picked.smart}
            smartValue={isSmartValue(condition.value) ? condition.value.smart : undefined}
            onPick={(name) => onChange({ field, op, value: name })}
            onPickSmart={(key) => onChange({ field, op, value: { smart: key } })}
            // "is (Empty)" is "is empty": the operator changes, so a null value never reaches the wire.
            onPickEmpty={() => onChange({ field, op: op === 'Neq' ? 'IsNotNull' : 'IsNull' })}
          />
```

- [ ] **Step 4: Run the tests**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ValuePicker.test.tsx src/features/issues/ConditionRow.test.tsx src/features/issues/FilterBuilder.test.tsx && npm run typecheck && npm run lint`
Expected: PASS (the existing single-select and multi-picker tests pass unchanged: no `smart` prop, no Smart section).

- [ ] **Step 5: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues/ValuePicker.tsx src/JiraUnpacked.Web/src/features/issues/ValuePicker.test.tsx src/JiraUnpacked.Web/src/features/issues/ConditionRow.tsx src/JiraUnpacked.Web/src/features/issues/ConditionRow.test.tsx
git commit -m "feat(web): Smart section in the sprint pickers" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: The Category column, and richer values end to end in the list

**Files:**
- Modify: `src/JiraUnpacked.Web/src/features/issues/columns.ts`
- Modify: `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`
- Modify: `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`

**Interfaces:**
- Consumes: `IssueListItem.statusCategory` (Task 5); server sort `statusCategory` and export column (Task 4).
- Produces: `ISSUE_COLUMNS` entry `{ id: 'statusCategory', header: 'Category' }` directly after `status`; sortable; not in `DEFAULT_COLUMNS`.

- [ ] **Step 1: Write the failing tests**

In `src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx`, add `statusCategory: 'To Do',` to `ITEM` after `status: 'Backlog',`, and append to `describe('ColumnChooser', …)`:

```tsx
  it('offers a Category column, off by default', async () => {
    mockList();
    const user = userEvent.setup();
    renderList();

    await openChooser(user);

    expect(screen.getByRole('checkbox', { name: /category/i })).not.toBeChecked();
    expect(screen.queryByRole('columnheader', { name: /category/i })).not.toBeInTheDocument();
  });

  it('shows each issue’s status category, next to Status, and sorts by it', async () => {
    const sent: Record<string, unknown>[] = [];
    server.use(
      http.get('/api/filter-catalog', () => HttpResponse.json(CATALOG)),
      noSavedViews,
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        sent.push((await request.json()) as Record<string, unknown>);
        return HttpResponse.json({ items: [ITEM], page: 1, pageSize: 50, totalCount: 1 });
      }),
    );
    const user = userEvent.setup();
    renderList('/projects/p1/issues?cols=jiraKey,statusCategory,status');

    await screen.findByRole('columnheader', { name: /category/i });
    // Table order, not URL order; no arrows — the default sort (updatedAt) is not shown here.
    expect(screen.getAllByRole('columnheader').map((h) => h.textContent)).toEqual(['Key', 'Status', 'Category']);
    expect(screen.getByRole('cell', { name: 'To Do' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /sort by category/i }));
    await waitFor(() => expect(sent.at(-1)).toMatchObject({ sortBy: 'statusCategory' }));
  });
```

In `src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx`, add a helper after `renderList`:

```tsx
function renderListAt(url: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path="/projects/:id/issues" element={<IssueList />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
```

and add inside `describe('IssueList', …)`:

```tsx
  it('sends relative, range and smart values from a link exactly as they are written', async () => {
    const bodies: { filter?: unknown }[] = [];
    server.use(
      http.post('/api/projects/p1/issues/query', async ({ request }) => {
        bodies.push((await request.json()) as { filter?: unknown });
        return HttpResponse.json({ items: [row('ACME-1')], page: 1, pageSize: 50, totalCount: 1 });
      }),
    );
    const filter = {
      logic: 'And',
      conditions: [
        { field: 'updatedAt', op: 'InLast', value: { amount: 14, unit: 'days' } },
        { field: 'createdAt', op: 'InPeriod', value: { period: 'lastMonth', timeZone: 'Europe/Warsaw' } },
        { field: 'createdAt', op: 'Between', values: ['2026-08-01', null] },
        { field: 'sprint', op: 'In', values: [{ smart: 'open' }, null] },
        { field: 'statusCategory', op: 'NotIn', values: ['Done'] },
      ],
    };

    renderListAt(`/projects/p1/issues?filter=${encodeURIComponent(JSON.stringify(filter))}`);

    await waitFor(() => expect(bodies.at(-1)?.filter).toEqual(filter));
  });
```

In `src/JiraUnpacked.Web/src/features/issues/viewState.test.ts`, add:

```ts
describe('the Category column', () => {
  it('keeps its place right after Status whatever order a view lists it in', () => {
    expect(shownColumns(['statusCategory', 'jiraKey', 'status'])).toEqual(['jiraKey', 'status', 'statusCategory']);
  });
});
```

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npx vitest run src/features/issues/ColumnChooser.test.tsx src/features/issues/IssueList.test.tsx src/features/issues/viewState.test.ts`
Expected: the Category tests FAIL (no such checkbox / header; `shownColumns` drops the unknown id); the link test PASSES already (Task 5's model) — it stays as the end-to-end pin.

- [ ] **Step 2: Add the column**

In `src/JiraUnpacked.Web/src/features/issues/columns.ts`, add directly after the `status` entry:

```ts
  // Where the list shows WHY a row matched a "not done" filter. Mirrors ExportColumns' "Category".
  { id: 'statusCategory', header: 'Category', render: (i) => i.statusCategory ?? EMPTY },
```

- [ ] **Step 3: Run the web checks**

Run: `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
cd C:/jw-filters
git add src/JiraUnpacked.Web/src/features/issues/columns.ts src/JiraUnpacked.Web/src/features/issues/ColumnChooser.test.tsx src/JiraUnpacked.Web/src/features/issues/IssueList.test.tsx src/JiraUnpacked.Web/src/features/issues/viewState.test.ts
git commit -m "feat(web): Category column; richer filter values end to end in the list" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Final verification (after Task 8, before handing back)

- [ ] `cd C:/jw-filters && JU_TEST_DATABASE=ju_test_richer dotnet test JiraUnpacked.sln -v q` → PASS (whole suite, integration included).
- [ ] `cd C:/jw-filters && dotnet ef migrations has-pending-model-changes -p src/JiraUnpacked.Infrastructure -s src/JiraUnpacked.Api` → `No changes have been made to the model since the last migration.` (no schema change in this sub-project).
- [ ] `cd C:/jw-filters/src/JiraUnpacked.Web && npm run typecheck && npm run lint && npm run test && npm run build` → PASS.
- [ ] `git -C C:/jw-filters diff origin/develop -- src/JiraUnpacked.Web/package.json src/JiraUnpacked.Web/package-lock.json` → empty (no dependency changes).
- [ ] `git -C C:/jw-filters grep -n "localStorage" -- src/JiraUnpacked.Web/src/features` → no new hits.
- [ ] The production image carries zone data (Review Focus 1): `cd C:/jw-filters && docker build --target runtime -t ju-richer-tzcheck . && docker run --rm --entrypoint sh ju-richer-tzcheck -c "test -f /usr/share/zoneinfo/Europe/Warsaw && echo zoneinfo-ok" && docker rmi ju-richer-tzcheck` → `zoneinfo-ok`. A throwaway tag: nothing running is touched, nothing is deployed.
- [ ] `git -C C:/jw-filters status` clean; `git -C C:/jw-filters log --oneline origin/develop..` shows the spec commit, this plan's commit and eight task commits. Nothing pushed.

---

## Self-Review

**Spec coverage.**
§1 Dates — operators appended: Task 2; Between on dates (open bounds, date-only inclusive end, UTC, from > to 400) and on storyPoints (inclusive, decimals): Task 2; InLast/NotInLast (units, 1…3650, AddMonths): Tasks 1–2; InPeriod (ten periods, Monday weeks, IANA zone, missing → UTC, unknown → 400): Tasks 1–2; the zone travelling in the condition and saved with the view: Task 5 (`freshCondition`), Task 6 (hint, capture), Task 4 (saved-view round trip).
§2 Sprint smart values — object wire form, strings stay names, null stays (Empty), sprint-only and unknown-key 400s, no-match is not an error, tenant-scoped resolution: Task 3 (per-project via the issue query's project filter — see Decisions); `filter-values` `smart` counts, omitted elsewhere: Task 3.
§3 Status category — field, operators, empty rule incl. uncategorised statuses, id-set translation without a join, value list with `emptyCount`, column "Category", sort, projection: Task 4 (server), Task 8 (column); export column: Task 4.
§4 SPA — labels: Task 5; Between/amount+unit/period controls, 14-day default, "more than … ago", period select in reading order, silent zone + hint: Task 6; Smart section in both pickers, chip labels: Tasks 5 and 7; URL/tree model accepting object values, completeness rules: Task 5; operator validation from the exhaustive label map: Task 5 (labels added; `isKnownOperator` unchanged).
§5 Testing — every translator case listed: Tasks 2–4 (Between dates/decimals, every unit, every period with Monday weeks, quarter and year boundaries, a non-UTC zone where the day differs, missing zone, unknown zone, each smart key, no active sprint, smart off sprint, string "active", statusCategory ops with the empty rule); integration: Tasks 2–4 (fixed-clock harness, smart and category counts, smart and relative queries, category column/sort/export, saved view); web: Tasks 5–8 (wire shapes, completeness/pruning, smart section in both pickers, URL round-trip, labels).

**Placeholder scan.** No TBD/TODO. Every code step shows its code. One step points at existing code instead of repeating it — Task 5 Step 5(f) moves the existing null-normalisation block over unchanged, and names where it starts and ends.

**Type consistency.** `FilterLookups.SprintState` / `.StatusCategory` / `.UncategorisedStatus` are defined in Tasks 3–4 and used by the compiler and both unit-test resolvers under those names. `DictPick`/`ReadDictValues`/`ReadSmart` (Task 3) are reused by `BuildStatusCategoryAsync` (Task 4). `IssueFilterTranslator(IClock)` (Task 2) is what `RicherFilterTestData.WithFixedClock` and every unit test construct. `IssueListItem.StatusCategory` is appended last (Task 4), and the only constructor call is `ToListItem`. On the web: `ListValue`/`ConditionValue`/`SmartValueOption` (Task 5) are what `MultiValuePicker`, `SingleValueSelect`, `ConditionRow` and `filterTree` use in Tasks 5–7; `takesRange`/`takesAmount`/`takesPeriod`, `freshCondition`, `switchOperator` (Task 5) are what `ConditionRow` imports in Tasks 5–6; accessible names in Task 6 are the ones Task 6's tests query.

**Review Focus.** Five lines above. Each has its pinning test or check named in the owning task (1: Task 1 Step 6 + Final verification; 2: Task 1; 3: Task 3; 4: Task 5; 5: Tasks 1–2).
