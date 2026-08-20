# Web UI — Spec 3: Explore design

**Status:** Approved (2026-08-08)
**Epic:** Web UI — the browser front-end over the completed API.
**This spec:** Spec **3 of 4** — making the synced data visible: the issue list, the FR-E4 filter builder, and issue detail.

Spec 1 shipped auth and the shell; Spec 2 shipped the setup flow, so data now arrives. But nothing displays it — after a successful sync the user sees a run table reporting "412 issues processed" and no way to look at a single one. This spec closes that.

---

## 1. Scope & non-goals

**In scope:**

- **Issue list** — the project's issues, paged, with the resolved display names the API already projects (status, type, priority, assignee, sprint, team).
- **Filter builder** — a UI over FR-E4's `POST /issues/query`: a flat list of ANDed conditions across the closed field catalog.
- **Issue detail** — one issue with its parent, its children, and its full field-change history.

**Out of scope:**

| Spec | Scope |
|---|---|
| 4 — Insight | the four curated metrics, gap-checks, admin panel |

**Explicit non-goals (YAGNI):** no saved or named filters (the API has no table for them — FR-E4 deferred it deliberately); no cross-field OR or nested groups (FR-E4 is flat-AND by design, and the `In` operator covers "status is A or B"); no column chooser, sorting UI, or CSV export (the API fixes the order at `JiraUpdatedAt DESC, JiraKey` and exposes no sort parameter — a sort control would be a lie); no inline editing (this is a read-only warehouse; it never writes back to Jira).

**No backend change.** All three endpoints exist and are tested. If a screen appears to need a new endpoint, a new sort, or a new field, re-scope the screen and report it.

## 2. Decisions locked in

- **One code path: always `POST /issues/query`.** It subsumes the plain GET — FR-E4 guarantees and tests that empty/null `conditions` returns the unfiltered page, ordered and paged identically. Maintaining a second GET path would double the surface for no behavioural gain.
- **The field catalog is mirrored on the client, and the server stays authoritative.** The catalog lives in `FilterFields.All` (Infrastructure) and no endpoint exposes it, so the UI carries its own copy to render the right operators and inputs. Same shape as Spec 1's password-policy mirror, same rule: **the mirror is a convenience; every server 400 renders verbatim** and the mirror never suppresses one. The divergence risk is stated in §4 rather than hidden.
- **Filter state lives in React state, not the URL.** Encoding a condition list into a query string is real work, and FR-E4 deferred saved filters. Issue *detail* is a real route so a specific issue is linkable; a *filtered view* is not. Recorded as a follow-up rather than pretended away.
- **Issues get their own routes, not a fourth `ProjectDetail` tab.** `ProjectDetail` is the configuration and sync surface; browsing issues is the primary workspace and deserves its own screen.

## 3. The API surface this spec consumes

Verified against `IssuesController` and the domain view records.

- `POST /api/projects/{projectId:guid}/issues/query` — body `IssueFilterRequest { conditions?: FilterCondition[]; page?: number; pageSize?: number }` → **200** `PagedResult<IssueListItem>`; **400** ProblemDetails whose `title` names the offending field and reason; **404** unknown/cross-tenant project.
  - `FilterCondition { field: string; op: FilterOperator; value?: unknown; values?: unknown[] }`
  - `FilterOperator` (serialised by name): `Eq | Neq | In | Lt | Lte | Gt | Gte | Contains | StartsWith | IsNull`
- `GET /api/projects/{projectId:guid}/issues/{jiraIssueId}` → **200** `IssueDetail`; **404**.
- `PagedResult<T> { items: T[]; page: number; pageSize: number; totalCount: number }`
- `IssueListItem { jiraIssueId, jiraKey, summary, status, issueType, priority, assignee, sprint, team, storyPoints, parentJiraIssueId, jiraCreatedAt, jiraUpdatedAt }` — dictionary fields arrive as **resolved display names**, not ids.
- `IssueDetail { issue: IssueListItem; parent: IssueRef | null; children: IssueRef[]; changeHistory: ChangeHistoryEntry[] }`
- `IssueRef { jiraIssueId, jiraKey, summary }` · `ChangeHistoryEntry { field, fromValue, toValue, author, changedAt }`

Paging clamps: `page < 1 → 1`; `pageSize < 1 → 50`, capped at **100**. Order is fixed at `JiraUpdatedAt DESC, JiraKey` and is not client-controllable.

## 4. The filter builder — the design centrepiece

FR-E4's catalog is a closed allow-list. The UI's job is to make an invalid filter hard to express, while never claiming authority it does not have.

| Field | Type | Operators | Value input |
|---|---|---|---|
| `status`, `issueType`, `priority`, `assignee`, `sprint`, `team` | DictName | `Eq`, `Neq`, `In`, `IsNull` | text (display **name**); `In` takes a list |
| `storyPoints` | Decimal | `Eq`, `Neq`, `Lt`, `Lte`, `Gt`, `Gte`, `IsNull` | number |
| `createdAt`, `updatedAt` | DateTime | `Lt`, `Lte`, `Gt`, `Gte` | date |
| `summary` | Text | `Contains`, `StartsWith`, `IsNull` | text |
| `jiraKey` | Text | `Eq`, `Contains`, `StartsWith` | text |
| `parent` | ParentRef | `Eq`, `IsNull` | text (Jira issue id) |

**Interaction rules.** A condition row is *field → operator → value*. Choosing a field narrows the operator list to that field's set; switching to a field that disallows the current operator **resets the operator** rather than silently sending an invalid pair. `IsNull` hides the value input entirely (FR-E4 ignores any value). `In` renders a multi-value editor. Guards mirrored from FR-E4: at most **20 conditions** and at most **100 values** per `In` — the client blocks going beyond, and a server 400 still renders if it disagrees.

**Dictionary fields filter by display name**, matching what the list shows, because that is what the user sees. FR-E4 resolves names to ids tenant-scoped, so a name belonging to another tenant simply matches nothing.

**`Neq` on a dictionary field includes unassigned issues** — FR-E4 chose this deliberately (`status ≠ Done` returns issues with no status at all). The UI says so inline, because the opposite reading is equally plausible and a silent choice would mislead.

**The mirror-divergence risk, stated plainly.** The client catalog copies a server-side one that has no endpoint behind it. If a field is *added* server-side and not mirrored, it is merely absent from the UI — harmless. If one is *removed or renamed*, the UI offers a field the server rejects with a 400 — visible and non-destructive. The mirror is covered by a test asserting the exact field/operator matrix, so drift surfaces as a failing test rather than a support ticket. A catalog endpoint would remove the duplication and is recorded as a follow-up.

## 5. Screens

**Issue list** (`/projects/:id/issues`) — a `Table` of key, summary, status, type, assignee, sprint, points, updated. The key links to detail. Above it sits the filter builder (collapsed to a summary line when no conditions are set); below it, pager controls with the total count.

Empty results must distinguish **"this project has no issues yet — run a sync"** from **"no issues match this filter"**: those need opposite actions, and one shared empty state would send a user to re-sync a project that is working fine. A **400** renders the server's `title` verbatim beside the builder (it names the offending field), and the previous results stay on screen rather than blanking.

**Issue detail** (`/projects/:id/issues/:jiraIssueId`) — header with key, summary, and the resolved dictionary values as `Badge`s; a **hierarchy** section linking parent and children (each `IssueRef` → its own detail route); and a **change-history** table of field, from → to, author, timestamp, newest first. Empty history reads "No recorded changes" — meaningfully different from an error. A `null` `fromValue` renders as "—", never the string "null".

**Navigation:** `ProjectDetail`'s header gains a **Browse issues** link, and Home's project table links straight to each project's issue list — the shortest path from "I just synced" to "show me the data".

## 6. Architecture

```
src/JiraUnpacked.Web/src/
  api/issues.ts              query/detail wrappers + filter types
  features/issues/
    filterCatalog.ts         the mirrored catalog — pure, exhaustively tested
    FilterBuilder.tsx        condition rows, add/remove, guards
    ConditionRow.tsx         one field/operator/value row
    IssueList.tsx            table + pager + empty/error states
    IssueDetail.tsx          header + hierarchy + change history
```

`api/issues.ts` stays React-free like its siblings. `filterCatalog.ts` is pure data plus lookup helpers, so the field/operator matrix and the operator-reset rule are testable without rendering. Query keys extend `src/api/queryKeys.ts` — the single source established in Spec 2.

## 7. Testing

Vitest + React Testing Library + MSW; logic over markup; no snapshots.

- **Catalog (pure):** the exact field → (type, operators) matrix asserted field by field, so drift from `FilterFields.All` fails loudly; plus the operator-reset rule when the field changes.
- **Request shape — the likeliest defect:** a built filter POSTs exactly the expected `conditions` array. `IsNull` sends **no** `value`; `In` sends `values` (not `value`); a single-value operator sends `value` (not `values`). Getting this wrong yields a runtime 400 that no type check catches.
- **Guards:** blocked at 21 conditions and at 101 `In` values, **and** a server 400 still renders when the client thought the filter was fine.
- **Empty-state discrimination:** unfiltered-and-empty says "run a sync"; filtered-and-empty says "no matches". A single shared empty state is a defect.
- **Paging:** page 2 requests `page: 2`; `totalCount` drives the pager; `pageSize` never exceeds 100.
- **Detail:** parent and children link to their own routes; empty history reads "No recorded changes"; a null `fromValue` renders "—", not "null".
- No .NET change, so the .NET suite (**278**) stays green and untouched.

## 8. Success criteria

- After a sync, a user opens a project, sees its issues, narrows them with a composed condition set, pages through the results, and opens one issue to read its history and hierarchy.
- An invalid filter is hard to express, and when the server rejects one anyway the message names the offending field.
- "No issues yet" and "no matches" never look the same.
- No backend change, no new runtime dependency, .NET suite still 278 green, FR-B2 gate untouched.

## 9. Notes / follow-ups

- **Deferred to Spec 4:** the four curated metrics, gap-checks, admin panel.
- **Worth a backend spec later:** a filter-catalog endpoint (removes the client mirror entirely); saved/named filters; a sort parameter on the issue list; cross-field OR / nested groups.
- **Filter state in the URL** would make a filtered view shareable; deferred here per §2.
