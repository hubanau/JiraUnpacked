# FR-E4 — Generic filter builder (issue list) design

**Status:** Approved (2026-08-08)
**Epic:** Reporting & filters — the first **post-v1** feature (v1 shipped to main @ `5d564a6`).
**This spec:** the filtered issue list.

REQUIREMENTS FR-E4: *"User-composable boolean filters."* Deferred out of v1; now the next unit. It extends Module E's read layer (`IssueQueryService.ListAsync`) with a server-side, tenant-scoped filter over the modeled `Issues`, applied to the issue list.

---

## 1. Scope & non-goals

**In scope:** a POST endpoint that returns the same `PagedResult<IssueListItem>` as the existing list, narrowed by a client-supplied filter — a flat list of conditions, all ANDed. Each condition is `{ field, operator, value(s) }` over the fixed modeled field set. A `FilterTranslator` turns the validated filter into a strongly-typed `Expression<Func<Issue, bool>>` applied to the tenant-scoped `_db.Issues`.

**Out of scope (deferred follow-ups):**
- **Cross-field boolean OR / nested groups.** v1.x is flat-AND; the `In` operator covers "status is A or B" without an OR-tree. A future spec can wrap conditions in OR groups — the condition node is already the right shape for it.
- **Saved / named filters.** Ad-hoc filter in the request body only — so **no new tenant-owned table**, and the FR-B2 gate is untouched. Persisted filters would be their own spec (new table → both isolation layers + gate extension + CRUD).
- **Filtering metrics (FR-E2) or gap-checks (FR-E3).** Those fold the whole project / hierarchy; "throughput of a filtered subset" is a separate semantic question. v1.x filters the **list only**.

## 2. Decisions locked in brainstorming

- **Flat AND + `In`**; OR/nesting deferred.
- **Ad-hoc only**; no persistence, no new tenant table.
- **Issue list only.**
- **The field catalog is a closed allow-list** — the single place a field name is interpreted, and the safety boundary.
- **Predicates become parameterized LINQ Expressions; never raw SQL / `FromSql` / string interpolation.** This is the design's central safety property: a user-composable filter is an injection surface, and the only defense that matters is that user input never reaches a SQL string.

## 3. Request contract

```csharp
public enum FilterOperator { Eq, Neq, In, Lt, Lte, Gt, Gte, Contains, StartsWith, IsNull }

public sealed record FilterCondition(
    string Field, FilterOperator Op, JsonElement? Value, List<JsonElement>? Values);

public sealed record IssueFilterRequest(
    List<FilterCondition>? Conditions, int Page = 1, int PageSize = 50);
```

- `Op` deserializes from its name (the API already registers `JsonStringEnumConverter`); an unknown operator → 400.
- `Value` for single-value operators; `Values` for `In`. Values are `JsonElement` so the contract is generic; the translator coerces each to the field's declared CLR type and **400s on mismatch**.
- `Conditions` null or empty → unfiltered (this endpoint subsumes the plain GET list).
- Clamps identical to `ListAsync`: `Page < 1 → 1`; `PageSize < 1 → 50`, capped at 100.

## 4. The field catalog (allow-list = safety boundary)

A static registry maps each field name to `(FieldType, allowed operators)`:

| Field | Type | Operators |
|---|---|---|
| `status`, `issueType`, `priority`, `assignee`, `sprint`, `team` | `DictName` | `Eq`, `Neq`, `In`, `IsNull` |
| `storyPoints` | `Decimal` | `Eq`, `Neq`, `Lt`, `Lte`, `Gt`, `Gte`, `IsNull` |
| `createdAt`, `updatedAt` | `DateTime` | `Lt`, `Lte`, `Gt`, `Gte` |
| `summary` | `Text` | `Contains`, `StartsWith`, `IsNull` |
| `jiraKey` | `Text` | `Eq`, `Contains`, `StartsWith` |
| `parent` | `ParentRef` | `Eq`, `IsNull` |

This registry is the **only** place a field name is interpreted. An unknown `Field` → 400 (never a reflected member-access on an arbitrary name). An operator not in a field's set → 400.

`DictName` fields filter by the **display name** the user sees in the list (not surrogate Ids), because that is what the client renders.

## 5. The translator

`IIssueFilterTranslator.Build(Guid projectId, IReadOnlyList<FilterCondition> conditions, CancellationToken)` → `Expression<Func<Issue, bool>>` (or a validation error). Per condition:

1. **Validate** field (catalog lookup) → operator (allowed for the field's type) → value type (coerce `JsonElement`); any failure → a typed validation error the controller maps to **400** with a field/reason message.
2. **Build the predicate:**
   - **`DictName` → resolve names to Ids first**, not a correlated subquery. E.g. `status In ["In Progress","Done"]`: one tenant-scoped query `_db.Statuses.Where(s => names.Contains(s.Name)).Select(s => s.Id)` → `List<Guid>` → predicate `ids.Contains(i.StatusId)`. Filters directly on the indexed FK; a name that doesn't resolve contributes no Id (matches nothing) — and because the dict read is tenant-scoped, there is no cross-tenant name enumeration. `IsNull` → `i.StatusId == null` (unassigned / no sprint / no team). **`Neq` includes unassigned rows** — `i.StatusId == null || !ids.Contains(i.StatusId)` — rather than the raw `!Contains`, because SQL `NOT IN` drops NULL FKs and a user asking "status ≠ Done" intuitively expects unassigned issues in the result, not silently excluded. (Without cross-field OR in v1.x this is the only way to express "not X, including unassigned," so it is the sensible default.)
   - **`Decimal` / `DateTime`:** direct comparisons — `i => i.StoryPoints >= v`, `i => i.JiraUpdatedAt < v`. `DateTime` values parse as UTC.
   - **`Text` `Contains`/`StartsWith`:** `EF.Functions.ILike(i.Summary, pattern)` with the user value **LIKE-escaped** (`\`, `%`, `_` neutralised) and passed as a parameter — case-insensitive, injection-free. `Eq` on `jiraKey` → `i.JiraKey == v`. `IsNull` on `summary` → `i.Summary == null`.
   - **`ParentRef`:** `Eq` → `i.ParentJiraIssueId == v`; `IsNull` → `i.ParentJiraIssueId == null` (top-level issues).
3. **Compose** all predicates with `AndAlso` into one `Expression<Func<Issue, bool>>`.

**Why this is injection-safe:** field names resolve only through the closed catalog; operators are per-type allow-listed; values are coerced to CLR types and bound as parameters (EF parameterizes Expression constants); dict names resolve via scoped LINQ; text uses parameterized `ILike` with escaped wildcards. There is no code path from user input to a SQL string.

**Guards:** at most **20 conditions** per request and at most **100 values** per `In` (bounds worst-case cost) — exceeding either → 400.

## 6. Read model & endpoint

`IIssueQueryService` gains:
```csharp
Task<PagedResult<IssueListItem>?> QueryAsync(
    Guid projectId, IssueFilterRequest request, CancellationToken ct = default);
```
`QueryAsync` mirrors `ListAsync`: the tenant-scoped existence check (`_db.JiraProjects.AnyAsync` → null → 404), then `var q = _db.Issues.Where(i => i.JiraProjectId == projectId)`, then **`q = q.Where(translator predicate)`**, then `CountAsync`, then the same `OrderByDescending(JiraUpdatedAt).ThenBy(JiraKey).Skip().Take()` and the same correlated-subquery projection to `IssueListItem`. The base predicate carries the EF tenant filter and RLS; the added `.Where` cannot widen visibility.

**Endpoint:** `POST /api/projects/{projectId:guid}/issues/query`, `[Authorize]`, body `IssueFilterRequest` → `200 PagedResult<IssueListItem>`; unknown/cross-tenant project → **404**; a malformed filter (unknown field, disallowed operator, bad value type, over a guard limit) → **400** with a message naming the offending field/reason. The existing `GET .../issues` stays for the unfiltered case.

**Ordering of checks:** the tenant-scoped **existence check runs first** (unknown/cross-tenant project → 404), then validation/translation (→ 400). So a cross-tenant caller gets a uniform 404 whatever their filter looks like — a malformed filter never becomes an oracle for whether another tenant's project exists — and a bad filter against the caller's *own* project gets a helpful 400.

**Indexing:** no new index. The `(EndUserId, JiraProjectId, JiraUpdatedAt)` index (RV1) serves the project scope + default order; filters apply within a single project's already-index-narrowed rows, so "reports must not table-scan" holds — a per-field filter index is YAGNI at self-hosted scale.

## 7. Isolation & constraints

- All reads through the injected tenant-scoped `AppDbContext`. **No `IgnoreQueryFilters`, privileged factory, `FromSql`, or `.Include`** in the filter path; the dict-name resolution is ordinary tenant-scoped LINQ.
- **No new tenant table** → the FR-B2 gate test is untouched.
- A filter is applied on top of `Where(JiraProjectId == projectId)` on the tenant-filtered `Issues` set, so both isolation layers bind — a security test proves tenant B cannot use a filter to read tenant A's issues.

## 8. Testing

- **Per-operator translation** (integration, exact seeded data): `Eq`/`Neq`/`In`/`IsNull` on a dict field (by name); `Lt`/`Gte`/range on dates and story points; `Contains`/`StartsWith` on summary (case-insensitive); `parent IsNull` (top-level only) and `parent Eq`.
- **AND composition:** two conditions narrow to their intersection; empty conditions → same as the unfiltered list; paging + `TotalCount` correct over the filtered set.
- **Validation → 400:** unknown field; operator not allowed for the field; value type mismatch (`"abc"` for storyPoints); over 20 conditions; over 100 `In` values.
- **The security-critical tests:**
  - **Tenant isolation:** tenant B posts a filter against tenant A's project → **404**; and a filter can never surface another tenant's rows (the base tenant filter + RLS hold).
  - **Injection attempt is inert:** a `Contains` value containing SQL metacharacters and LIKE wildcards (`'; DROP …`, `%`, `_`) returns a normal (empty or literal-match) result — never an error, never extra rows — proving the value was parameterized and LIKE-escaped, not interpolated.
- **Dict-name resolution:** a value naming a status that exists only in *another* tenant's connection resolves to no Id (matches nothing), confirming the resolution is tenant-scoped.
- No migration; FR-B2 gate untouched; full suite green; Release clean.

## 9. Success criteria

- An End User composes a flat AND filter over the modeled fields and gets the matching page of their project's issues, ordered and paged like the plain list.
- Unknown fields, bad operators, wrong value types, and over-limit filters are rejected with 400; nothing reaches a SQL string.
- Tenant isolation holds under filtering; an injection attempt is inert.
- No new tenant table; FR-B2 gate unchanged and green; no migration.

## 10. Notes / follow-ups

- Deferred, in likely order: **saved/named filters** (a `SavedFilter` tenant table + CRUD + both isolation layers + gate extension); **cross-field OR / nested groups**; **applying filters to metrics / gap-checks**.
- If a specific field becomes a hot filter at larger scale, add a targeted `(EndUserId, JiraProjectId, <field>)` index then — not pre-emptively.
