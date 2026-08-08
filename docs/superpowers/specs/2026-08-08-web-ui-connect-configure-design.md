# Web UI — Spec 2: Connect & Configure design

**Status:** Approved (2026-08-08)
**Epic:** Web UI — the browser front-end over the completed API.
**This spec:** Spec **2 of 4** — everything needed to go from an empty shell to a configured project with data flowing in.

Spec 1 (Foundation) shipped to `main` @ `4ed052b`: the scaffold, design system, typed API client with single-flight refresh, auth screens, and app shell. A user can sign in — and then sees "No projects yet". This spec makes that message go away.

---

## 1. Scope & non-goals

**In scope:** the four screens that turn an empty account into a synced project.

- **Connections** — list, create (Jira base URL + email + API token), detail.
- **Projects** — add from the connection's real Jira project list, list, detail, edit sync interval.
- **Semantic configuration** — field mappings (5 roles → Jira fields), status semantics (each Jira status → Active/Done/Other), and issue-type classes (→ Epic/Other, connection-scoped).
- **Sync** — trigger a sync, trigger a re-derivation, and watch run history.

**Out of scope — later specs:**

| Spec | Scope |
|---|---|
| 3 — Explore | issue list, FR-E4 filter builder, issue detail |
| 4 — Insight | the four curated metrics, gap-checks, admin panel |

**Explicit non-goals (YAGNI):** no connection editing or deletion (the API exposes neither — creating a second connection is the supported path); no bulk/CSV import of mappings; no live progress bar inside a running sync (the API reports run-level status only, and inventing sub-progress would be a lie); no optimistic updates on configuration writes — a mapping that appears saved but was rejected is worse than a brief spinner.

**No backend change whatsoever.** Every endpoint this spec consumes already exists and is tested. If a screen seems to need a new endpoint, that is a signal to re-scope the screen, not to widen the API — raise it rather than adding one.

## 2. Decisions locked in

- **The dependency chain is the design.** Connection → project → mappings → sync → data. Each step is useless without its predecessor, so the UI makes the chain explicit rather than presenting five unrelated CRUD forms (§4).
- **Field mappings and status semantics are replace-all, because the API is.** `PUT /field-mappings` and `PUT /status-semantics` delete the existing rows and insert what you send, so those editors load the current set, edit locally, and submit the complete list — never a partial patch. **`PUT /issue-types` is different: it is additive** (only listed types change; unlisted ones keep their class) and it validates the whole batch before mutating, so one unknown id rejects the request entirely. Its editor may still send the complete set — that is harmless, since every id is known and unchanged rows are written their current value — but nothing is lost by omitting a row, and no fetch-merge dance is required.
- **Poll while a run is Running; stop when none is.** There is no push channel, and a fixed forever-poll would hammer the API for nothing.
- **The API token is write-only.** The API never returns it (`ConnectionResponse` is `{Id, BaseUrl, Email, CreatedAtUtc}`), so the UI never renders it and says plainly at entry that it cannot be shown again.
- **Reuse Spec 1's primitives and client as-is.** No new runtime dependency; no new UI primitive unless a screen genuinely cannot be built from the existing eleven.

## 3. The API surface this spec consumes

Verified against the controllers — these are the real shapes.

**Connections** (`ConnectionResponse { id, baseUrl, email, createdAtUtc }`)
- `POST /api/connections` `{baseUrl, email, apiToken}` → **201**; **400** "Jira credentials could not be validated."; **502** "Jira could not be reached."
- `GET /api/connections` · `GET /api/connections/{id}`
- `GET /api/connections/{id}/fields` → `JiraField { id, name, custom }[]`
- `GET /api/connections/{id}/projects-available` → `JiraProjectSummary { id, key, name }[]`

**Projects** (`ProjectView { id, jiraConnectionId, jiraProjectKey, jiraProjectId, syncIntervalMinutes, createdAtUtc }`)
- `POST /api/connections/{connectionId}/projects` `{projectKey, syncIntervalMinutes}` → **201**; **404** connection; **400** unknown project key; **400** interval < 60; **409** duplicate
- `GET /api/connections/{connectionId}/projects` · `GET /api/projects/{projectId}`
- `PATCH /api/projects/{projectId}` `{syncIntervalMinutes}` → **204**; **400** interval < 60; **404**

**Semantic layer**
- `GET /api/projects/{id}/jira-statuses` → `JiraStatus { id, name }[]` (live from Jira → can 502)
- `GET|PUT /api/projects/{id}/field-mappings` — `{ mappings: [{ role, jiraFieldId }] }`, `role` ∈ `Team | Assignee | Sprint | StoryPoints | Status`
- `GET|PUT /api/projects/{id}/status-semantics` — `{ statuses: [{ jiraStatusId, statusName, class }] }`, `class` ∈ `Active | Done | Other`
- `GET|PUT /api/connections/{id}/issue-types` — GET returns `{ jiraIssueTypeId, name, class }[]`; PUT takes `{ issueTypes: [{ jiraIssueTypeId, class }] }`, `class` ∈ `Other | Epic`; **400** `{error}` on an unknown id
- All PUTs return **204**; all return **404** for an unknown/cross-tenant id.

**Sync** (`SyncRunView { id, jiraProjectId, status, startedAt, finishedAt, issuesProcessed, error, nextRunAt, kind }`, `status` ∈ `Running | Succeeded | Failed | PartiallyFailed`)
- `POST /api/projects/{id}/sync` → **200** with the run; **404**; **409** already running. Rate-limited **5/min per user**.
- `POST /api/projects/{id}/rederive` → same shape and codes. Rate-limited.
- `GET /api/projects/{id}/sync-runs` → `SyncRunView[]`

Enums serialise **by name** (the API registers `JsonStringEnumConverter`), so they are TypeScript string unions.

## 4. Guided setup — the one piece of real design

The endpoints are independent; the workflow is not. A user who adds a project and immediately hits **Sync** gets a run that succeeds while producing data with no status semantics — issues land, but every downstream metric in Spec 4 is meaningless. Nothing in the API prevents this, so the UI must.

Each project therefore carries a **readiness state** derived entirely from data already fetched — no new endpoint:

| Step | Ready when | Consequence if skipped |
|---|---|---|
| Field mappings | `GET /field-mappings` is non-empty | Team/assignee/sprint/points stay empty on every issue |
| Status semantics | `GET /status-semantics` covers every status from `GET /jira-statuses` | Cycle-time and throughput cannot be computed |
| Issue-type classes | at least one issue type on the connection is classed `Epic` | Epic gap-checks report *NotConfigured*, not "no problems" |

The project detail page shows this as a short checklist with each incomplete step linking to its editor. **Sync stays enabled throughout** — the checklist informs, it does not gate; a user re-syncing a deliberately partially-configured project is a legitimate case, and blocking it would be paternalistic. Status semantics is called out as the one whose absence silently corrupts later reporting.

The readiness computation is a **pure function** over the three fetched lists, so it is unit-testable without rendering.

## 5. Screens

**Connections list** (`/connections`) — cards showing base URL, email, and created date. (Deliberately *not* a project count: there is no aggregate endpoint, so it would cost one request per connection for decoration.) Empty state explains that a connection is a Jira site plus an API token, and links to Atlassian's token page. Primary action: **Add connection**.

**Add connection** — base URL, email, API token (`type="password"`). The submit is slow (the API validates against Jira live), so the button uses `Button`'s `loading` state. A **400** renders the server's "Jira credentials could not be validated." verbatim; a **502** renders the upstream-Jira message — the distinction matters, since one means "your token is wrong" and the other "Jira is down". The form states that the token is stored encrypted and cannot be displayed again.

**Connection detail** (`/connections/:id`) — the connection's projects, an **Add project** action, and the **issue-type classes** editor (a table of the connection's issue types, each with an Epic/Other selector, saved as one PUT).

**Add project** — a picker populated from `GET /projects-available`, showing `KEY — Name` (never a free-text key field; the key must be one Jira actually has, and the picker makes the 400 unreachable by construction), plus the sync interval with a **60-minute minimum** mirrored from the server. **409** renders as "That project is already added."

**Project detail** (`/projects/:id`) — header with key, connection, and interval; the **readiness checklist** (§4); tabs:
- **Overview** — sync controls and run history.
- **Fields** — five rows, one per `FieldRole`, each a searchable select over `GET /fields` (a real Jira instance has hundreds of fields, so an unfiltered `<select>` is unusable). Saved as one PUT of the complete set.
- **Statuses** — one row per status from `GET /jira-statuses`, each with an Active/Done/Other selector, defaulting to any existing saved semantic. Saved as one PUT. Unclassified statuses are visually flagged.

**Sync & history** (Overview tab) — **Sync now** and **Re-derive** buttons, and a table of runs: kind, status badge, started, duration, issues processed, and error. `Running` rows animate. **409** reads "A sync is already running for this project."; **429** shows Spec 1's rate-limit message (the limiter is 5/min per user, and a user clicking twice will hit it). `SyncRun.Error` is server-curated and safe to render verbatim. **Re-derive** is explained as "rebuild the modelled data from what was already pulled — use after changing mappings", because that is precisely when it is needed and it never calls Jira.

**Polling:** while any run is `Running`, the runs query refetches every **3 seconds**; when none is, polling stops. Triggering a sync invalidates the query immediately.

## 6. Architecture

```
src/JiraUnpacked.Web/src/
  api/
    connections.ts   list/get/create/fields/projectsAvailable
    projects.ts      list/get/add/updateInterval
    mappings.ts      fieldMappings, statusSemantics, jiraStatuses, issueTypes
    sync.ts          runSync, runRederive, listRuns
    queryKeys.ts     one place for TanStack Query keys
  features/
    connections/     ConnectionsList, AddConnection, ConnectionDetail, IssueTypeClassEditor
    projects/        AddProject, ProjectDetail, SyncIntervalField
    config/          FieldMappingsEditor, StatusSemanticsEditor, readiness.ts
    sync/            SyncControls, SyncRunTable, useSyncRuns
```

`api/*` stays React-free and mirrors Spec 1's pattern (thin typed wrappers over `apiFetch`). `features/*` holds screens and their hooks. `readiness.ts` is pure. Centralising query keys prevents the classic bug where a mutation invalidates a key that no query actually uses, leaving stale data on screen.

Spec 1's layering rules continue to bind: `src/ui/` imports neither `src/api/` nor `src/auth/`, and `api/client.ts` imports neither React nor `../auth`.

## 7. Testing

Vitest + React Testing Library + MSW, targeting logic rather than markup — no snapshots.

- **Readiness (pure):** every combination of missing/complete mappings, semantics, and Epic classification; specifically that partial status coverage is *not* ready, and that readiness never blocks sync.
- **Replace-all semantics:** the editors submit the **complete** set, including untouched rows — a test that changes one row and asserts the PUT body still contains all of them. This is the defect most likely to slip through, and it silently deletes a user's other mappings.
- **Error mapping per screen:** connection create 400 vs 502 produce *different* messages; add-project 409 reads as duplicate; sync 409 reads as already-running; sync 429 reads as rate-limited.
- **Polling lifecycle:** polls while a run is `Running`, stops when the last run finishes, and does not poll on a project with no runs. Uses fake timers; asserts request counts, not wall-clock behaviour.
- **The field picker** filters by name and submits the field **id**, not its label.
- **Sync-interval minimum:** 59 rejected client-side, 60 accepted, and the server's 400 still surfaces if it disagrees.
- No .NET change, so the .NET suite (**278**) must stay green and untouched.

## 8. Success criteria

- A user with a Jira API token can, without leaving the UI: add a connection, add one of its projects, map fields, classify statuses and epics, run a sync, and watch it complete.
- After that, the issue data exists in the warehouse — verifiable via the API, and consumed by Spec 3.
- A wrong token, an unreachable Jira, a duplicate project, a second concurrent sync, and a tripped rate limit each produce a distinct, accurate message.
- Configuration saves never silently drop rows the user did not touch.
- Polling stops when nothing is running.
- No backend change, no new runtime dependency, the .NET suite still 278 green, and the FR-B2 isolation gate untouched.

## 9. Notes / follow-ups

- **Deferred to Spec 3:** issue list, FR-E4 filter builder, issue detail. **Spec 4:** metrics, gap-checks, admin.
- **Known API gaps this spec works around, worth their own backend spec later:** no connection update or delete; no endpoint reporting configuration completeness (the UI derives it); no sub-run progress. None blocks this spec.
- If a real Jira instance makes the field picker slow at hundreds of fields, virtualise it then — not pre-emptively.
