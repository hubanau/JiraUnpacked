# Filtering metrics and gap-checks

**Status:** Approved (2026-08-22)
**Epic:** Post-UI backend follow-ups — **2 of 4**.
**Depends on:** cross-field OR and nested filter groups (spec 1 of 4) — this consumes that shape.

FR-E4's filter applies to the issue list alone. Spec 4 of the Web UI epic said so plainly: *"A filter control on a metrics screen would be a lie."* This spec makes it true instead, so a team can ask "what is *our* cycle time" rather than only the whole project's.

---

## 1. Scope & non-goals

**In scope:** five POST endpoints — the four curated metrics and the gap-check run — each accepting the same `conditions`/`filter` payload the issue query accepts.

**Out of scope:**
- **Filtering the gap-check *catalog*.** It lists what checks exist; a filter cannot change that.
- **Saved filters.** Spec 3 of 4. These endpoints take a filter inline, and go on doing so afterwards.
- **New metrics.** The four are the four.
- **UI.** Backend only.

## 2. Decisions locked in

- **The existing GET endpoints stay, unchanged.** They are the unfiltered report and the shipped SPA calls them. POST is added beside each, because a filter is a structured object and this codebase already puts one in a POST body (`/issues/query`).
- **The filter selects a population; the metric then computes over that population.** Not "compute, then filter the output" — those differ for every derived number, and only the first matches what a reader means by "our team's throughput".
- **One compiler, not three.** `IssueQueryService.QueryAsync` currently owns the tenant-scoped dict-name resolver inline. Metrics and gap-checks need the identical thing, so it is extracted into a shared `IIssueFilterCompiler` and all three call it. Copying it twice more is how the three drift apart.
- **No new table, no migration, no new dependency** — the FR-B2 gate is untouched.

## 3. Gap-checks: the filter selects the SUBJECTS, never the children

This is the section that matters, and it is the one place where the obvious implementation is wrong.

`DoneEpicOpenChildrenCheck` asks: is this epic Done while a child is still open? If the filter were applied to the issue *population* the check reasons over, then filtering to "assignee = Alice" would remove Bob's open children from the context — and an epic with an open child would be reported **clean**. The check would be answering a question nobody asked, with a green result, which is the single worst failure this codebase's reports can produce.

So the rule is:

> **The filter chooses which issues are examined. It never changes the evidence used to judge them.**

Concretely: `GapCheckContext` is still built from the **whole project** — the children lookup, the epic classification, all of it — and the runner drops violations whose *subject* is outside the filtered set, after the check has run. The checks themselves are untouched and stay pure, so it is structurally impossible for a check to see a truncated child list.

`status` is unaffected by filtering. A `NotConfigured` result stays `NotConfigured` with zero violations: the check still could not run, and a filter cannot make it configured. The Spec 4 rule holds unchanged — `NotConfigured` never renders as "no problems".

## 4. What each metric means over a filtered population

| Metric | Filtered meaning |
|---|---|
| story counts | statuses of the filtered issues; `totalIssues`/`totalStoryPoints` are the filtered totals |
| time in progress | average cycle time of the filtered issues that completed; `averageDays` is still **null**, never 0, when none qualify |
| throughput | completions per period **among the filtered issues** — an all-zero series is still a finding, not an empty state |
| status aging | ages of the filtered non-Done issues; the histogram spans the filtered population, still computed before paging |

Change history stays loaded per project rather than per filtered id-set: the fold is per-issue, so a wider load cannot change a number, and an `IN` list of issue ids could grow to the size of the project. Correctness is unaffected; only the read is slightly wider than strictly needed.

## 5. Validation and errors

Identical to `/issues/query`, because it is the identical compiler: an unknown field, a disallowed operator, a type mismatch, an empty group, or any guard breach returns **400** naming the offence; an unknown or cross-tenant project returns **404**, and the existence check runs **first** so a bad filter on someone else's project still says 404 rather than leaking the difference. `conditions` and `filter` together is 400.

Throughput's own validation (`bucket`, `from > to`) keeps its existing `{ error }` body shape on the POST route, so a client parses one shape per concern.

## 6. Testing

- **The load-bearing gap-check test:** an epic whose only open child is excluded by the filter is **still reported as a violation**, and the violation still carries that child. This fails against the naive "filter the context" implementation, which is the whole point.
- Filtering to a subject that is not an epic returns zero violations while the unfiltered run returns some — proving the subject filter is actually applied and the first test is not passing by doing nothing.
- `NotConfigured` survives filtering with its status intact.
- **Per metric:** a filtered report differs from the unfiltered one in the expected direction, asserted on exact seeded numbers. Specifically: story counts totals shrink to the filtered set; `averageDays` goes **null** (not 0) when the filter excludes every completed issue; throughput's bucket counts drop; aging's histogram and `totalCount` both narrow.
- The GET routes return exactly what they returned before — the unfiltered report — asserted alongside the POST-with-no-filter equivalent.
- 400/404 parity with `/issues/query`, including a bad filter on another tenant's project returning 404.
- FR-B2 gate untouched and green.

## 7. Success criteria

Every curated metric and every gap-check can be scoped to a filtered subset; a gap-check judged on a filtered subject still sees all of its children; `NotConfigured` and a null average remain impossible to misread; the GET routes are byte-for-byte unchanged.

## 8. Notes / follow-ups

Remaining in this epic: saved filters; httpOnly-cookie auth.
