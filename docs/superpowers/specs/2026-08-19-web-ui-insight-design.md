# Web UI — Spec 4: Insight design

**Status:** Approved (2026-08-19)
**Epic:** Web UI — the browser front-end over the completed API.
**This spec:** Spec **4 of 4** — the last. The curated metrics, the gap-checks, and the admin panel.

Specs 1–3 shipped to `main` @ `73a86e0`: a user can connect Jira, configure a project, sync it, and browse and filter the resulting issues. What is missing is the reason the warehouse exists — the reports. This spec adds them, and the small admin surface that has had no UI at all.

---

## 1. Scope & non-goals

**In scope:**

- **Four curated metrics** (FR-E2): story counts by status, average time in progress, throughput over time, and status aging with a histogram.
- **Gap-checks** (FR-E3): the catalog of available checks and their results, including violations with their offending children.
- **Admin panel** (FR-A4/FR-A5): generate an invite code; block, unblock, and delete an End User.

**Out of scope — and this closes the epic**, so these are genuine follow-ups rather than deferrals to a later spec in the plan:

- **Filtering metrics or gap-checks.** FR-E4's filter applies to the issue list only; the metrics endpoints take no condition set. A filter control on a metrics screen would be a lie.
- **Sorting or paging the metric bars.** The API returns them whole.
- **Any admin user list.** There is no endpoint that lists End Users — block/unblock/delete all take a `Guid` the admin must already have. §6 covers how the UI stays honest about that rather than inventing a roster.
- **Export, scheduling, or emailed reports.**

**No backend change.** Every endpoint exists and is tested. If a screen appears to need one that does not — an admin user list is the obvious temptation — **re-scope the screen and report it** rather than widening the API.

## 2. Decisions locked in

- **No new runtime dependency — including no charting library.** Spec 1's design anticipated Recharts for this spec. Revisiting that with the actual data in view: all four metrics are **bar-shaped** (counts by status, completions per period, a histogram of age bands) and the fourth is a single scalar. CSS bars plus an accompanying data table render these accurately, stay accessible and printable, need no JavaScript to be readable, and keep the dependency count at four. A charting library would add ~500 KB for four bar charts. If a future report genuinely needs multi-series or scatter, revisit then.
- **Every metric ships its numbers as a table beside its bars.** The bars are for shape; the table is the truth, and it is what a screen reader and a printout get.
- **`NotConfigured` is never rendered as "no problems".** §5 — this is the spec's central honesty requirement.
- **A null average is never rendered as zero.** §4.
- **Admin route gating is a UX affordance, not a security control.** The API enforces the `Admin` policy; the UI merely avoids showing a door that will not open.

## 3. The API surface this spec consumes

Verified against `MetricsController`, `ChecksController`, `InviteCodesController`, `AdminUsersController` and the domain view records.

**Metrics** — all `GET /api/projects/{projectId:guid}/metrics/…`, all **404** on an unknown/cross-tenant project.
- `story-counts` → `StoryCountsReport { byStatus: StatusCount[]; totalIssues: number; totalStoryPoints: number }`, where `StatusCount { status: string | null; class: string; count: number; storyPoints: number }`. **`status` is null** for issues with no status FK.
- `time-in-progress` → `TimeInProgressReport { averageDays: number | null; completedIssuesConsidered: number; issuesExcluded: number }`.
- `throughput?bucket=&from=&to=` → `ThroughputReport { bucket: string; from: string; to: string; buckets: ThroughputBucket[] }`, `ThroughputBucket { periodStart: string; completed: number }`. `bucket` ∈ `day | week | month`, default `week`; an unknown value → **400** `{ error: "bucket must be one of: day, week, month" }`; `from > to` → **400** `{ error: "from must not be later than to" }`.
- `status-aging?page=&pageSize=` → `StatusAgingReport { items: PagedResult<AgingItem>; histogram: AgingBand[] }`, `AgingItem { jiraIssueId, jiraKey, summary: string|null, currentStatus: string|null, class: string, ageDays: number, enteredAt: string }`, `AgingBand { band: string; count: number }`.

**Gap-checks**
- `GET /api/projects/{projectId:guid}/checks` → `GapCheckInfo[] { id, name, description }`; **404** unknown project.
- `GET /api/projects/{projectId:guid}/checks/{checkId}` → `GapCheckResult { check: GapCheckInfo; status: string; violations: PagedResult<GapCheckViolation> }`; **404** for an unknown project **or an unknown checkId**. `status` ∈ `Ok | NotConfigured`. `GapCheckViolation { jiraIssueId, jiraKey, summary, status, class, issueType, children: GapCheckChild[] }`, `GapCheckChild { jiraIssueId, jiraKey, summary, status, class }`.

**Admin** — all `[Authorize(Policy = "Admin")]`.
- `POST /api/admin/invite-codes` → `InviteCodeResponse { code: string; expiresAtUtc: string }`.
- `POST /api/admin/users/{id:guid}/block` · `/unblock` · `DELETE /api/admin/users/{id:guid}` → **204** on success; the delete additionally cascades across every tenant table.

Note the two error-body shapes already handled by Spec 1's `toApiError`: the metrics validation failures return plain `{ error }`, while most other failures are RFC 7807 ProblemDetails.

## 4. Two numbers that must never lie

The API was deliberately built so these cannot be misread, and the UI must not undo that.

**`averageDays` is `null`, not `0`, when no issue qualifies.** The domain records this explicitly — *"AverageDays is null (not 0) when no issue qualifies, so the number is never a lie."* Rendering `0` would tell a team their cycle time is instant. The UI renders **"Not enough data"** and shows `completedIssuesConsidered` so the user can see *why* — a project with 0 completed issues is a different situation from one with 40.

`issuesExcluded` is shown alongside, because an average over 12 issues with 300 excluded deserves visible doubt.

**A null `status` in `story-counts` means "no status", not a status named null.** It renders as **"(no status)"**, never the string `"null"` and never an empty label — those rows are real issues and dropping or mislabelling them changes the totals a reader computes by eye.

## 5. `NotConfigured` — the spec's central requirement

`GapCheckStatus` is `Ok | NotConfigured`, and the domain comment states the reason plainly: *"NotConfigured = it could not run (no issue type classified Epic), which must never be confused with 'no problems found'."*

Both states can return **zero violations**. They mean opposite things:

| Status | Violations | Meaning | What the user must do |
|---|---|---|---|
| `Ok` | 0 | The check ran and found nothing wrong | Nothing — this is good news |
| `NotConfigured` | 0 | The check **could not run** | Classify an issue type as Epic |

So a `NotConfigured` result must **never** render a success state, a green badge, or "No violations". It renders a **warning** that says the check could not run, explains that no issue type on the connection is classified as an Epic, and links to that connection's issue-type editor (Spec 2 built it). A user who reads "0 violations" and concludes their epics are healthy — when in fact nothing was checked — is the single worst outcome this screen can produce.

There is a test asserting the `NotConfigured` rendering shares no copy with the `Ok`-and-empty rendering.

## 6. Screens

**Metrics** (`/projects/:id/metrics`) — one page, four sections, each independently loaded so a slow or failing one does not blank the others:

- **Story counts** — a bar per status (count, with story points beside it), plus `totalIssues` and `totalStoryPoints`. Bars are CSS-width, scaled to the largest count, each with its numeric label adjacent; a table below carries the same numbers.
- **Time in progress** — the single scalar, or "Not enough data" (§4), with `completedIssuesConsidered` and `issuesExcluded`.
- **Throughput** — a bucket selector (day/week/month; default week) and optional from/to dates, over a bar per period. The two **400** cases render the server's `{ error }` message verbatim. An all-zero series still renders its bars rather than an empty state — "nothing was completed" is a finding, not an absence of data.
- **Status aging** — the histogram bands as bars, plus the paged `items` table (key, summary, current status, class, age in days, entered at). Paging reuses Spec 3's pager shape.

**Gap-checks** (`/projects/:id/checks`) — the catalog as a list of cards (name + description). Selecting one runs it and shows the result: the `NotConfigured` warning (§5), or the violations table where each violation expands to its offending children. Each violation's key links to Spec 3's issue detail. `Ok` with zero violations gets an explicit success state — *"No violations found"* — which must be visibly distinct from the `NotConfigured` warning.

**Admin** (`/admin`) — visible only when `useAuth().role === 'Admin'`:
- **Invite codes** — a Generate button; the returned code is displayed with its expiry and a copy affordance, and the screen states it is single-use and cannot be retrieved again.
- **User actions** — block, unblock, and delete by user id. Since **no endpoint lists users**, the UI asks for the id rather than pretending to offer a roster; the copy says where an admin obtains it instead of implying a directory exists. **Delete is destructive and irreversible** — it cascades across every tenant table — so it reuses Spec 1's typed-confirmation pattern (`Modal` + type `DELETE`), the same gate that guards account self-deletion.

**Navigation** — `ProjectDetail`'s header gains **Metrics** and **Checks** links beside the existing **Browse issues**; `SideNav` gains **Admin** for admins only.

## 7. Testing

Vitest + React Testing Library + MSW; logic over markup; no snapshots.

- **`NotConfigured` vs `Ok`-empty** — the centrepiece. Both return zero violations; assert the two renderings share **no** copy, that `NotConfigured` shows a warning and the link to the issue-type editor, and that it renders **no** success wording.
- **`averageDays: null`** renders "Not enough data" and **never** `0`; `averageDays: 0` (a real zero) still renders as a number, so the two cases are distinguishable.
- **Null status label** renders "(no status)", never `"null"` and never blank.
- **Throughput validation** — an invalid bucket and a reversed date range each render the server's `{ error }` verbatim.
- **All-zero throughput** renders bars, not an empty state.
- **Section isolation** — one failing metric section renders its own error while the other three still render their data.
- **Admin gating** — a non-admin does not see the nav entry or reach the route; and because that is UX only, a test asserts the screens still surface a server **403** rather than assuming the client check is sufficient.
- **Delete confirmation** — the confirm button stays disabled until `DELETE` is typed, and (Spec 3's lesson) the dialog state resets when reopened.
- No .NET change, so the .NET suite (**278**) stays green and untouched.

## 8. Success criteria

- A user with a synced, configured project can read all four metrics and run every gap-check in the catalog.
- A check that could not run says so, and can never be mistaken for a clean result.
- An average with no qualifying issues says so, and can never be mistaken for zero.
- An admin can mint an invite code and block, unblock, or delete a user, with the destructive action gated.
- No backend change, no new runtime dependency, .NET suite still 278 green, FR-B2 gate untouched.

## 9. Notes / follow-ups

This spec **closes the Web UI epic**. Remaining known work, all of it backend-side and none of it blocking:

- Return **502** rather than 401 for a rejected Jira credential, so the SPA need not discriminate by response body (carried since Spec 2).
- An **admin user list** endpoint — the one place this spec's UI is genuinely constrained by a missing API.
- A **filter-catalog endpoint**, removing Spec 3's client-side mirror.
- Saved/named filters; cross-field OR and nested groups; applying filters to metrics and gap-checks.
- httpOnly-cookie auth, replacing the SPA's `localStorage` refresh token.
