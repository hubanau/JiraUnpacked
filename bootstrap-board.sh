#!/usr/bin/env bash
#
# Bootstrap the Jira Unpacked board on GitHub.
# Creates labels, milestones (phases), and issues mapped to REQUIREMENTS.md FR IDs.
#
# Prerequisites:
#   1. Install GitHub CLI:  https://cli.github.com
#   2. Authenticate:        gh auth login
#   3. Run from inside your repo clone, OR pass the repo explicitly:
#        ./bootstrap-board.sh                 # uses the current repo
#        ./bootstrap-board.sh owner/repo      # targets a specific repo
#
# Safe to re-run: existing labels/milestones/issues (matched by title) are skipped.

set -euo pipefail

REPO="${1:-}"
if [[ -z "$REPO" ]]; then
  REPO=$(gh repo view --json nameWithOwner -q .nameWithOwner)
fi
echo "Target repo: $REPO"
echo

# ----------------------------------------------------------------------------
# Labels
# ----------------------------------------------------------------------------
create_label() {
  gh label create "$1" --color "$2" --description "$3" --repo "$REPO" 2>/dev/null \
    || gh label edit "$1" --color "$2" --description "$3" --repo "$REPO" >/dev/null
  echo "label: $1"
}

create_label "phase:1" "0E8A16" "Foundation"
create_label "phase:2" "1D76DB" "Identity & access"
create_label "phase:3" "B60205" "Tenancy gate"
create_label "phase:4" "5319E7" "Jira connection & semantic layer"
create_label "phase:5" "8B5CF6" "Sync / ETL engine"
create_label "phase:6" "0052CC" "Reporting & filters"
create_label "phase:7" "555555" "Hardening"
create_label "type:feature" "A2EEEF" "New functionality"
create_label "type:chore"   "FEF2C0" "Setup / tooling / infra"
create_label "type:test"    "D4C5F9" "Test coverage"
create_label "type:epic"    "C2E0C6" "Tracks a whole phase"
create_label "module:foundation" "EDEDED" ""
create_label "module:identity"   "EDEDED" ""
create_label "module:tenancy"    "EDEDED" ""
create_label "module:data"       "EDEDED" ""
create_label "module:cicd"       "EDEDED" ""
create_label "module:jira"       "EDEDED" ""
create_label "module:sync"       "EDEDED" ""
create_label "module:reporting"  "EDEDED" ""
create_label "module:security"   "EDEDED" ""
echo

# ----------------------------------------------------------------------------
# Milestones (one per active/near-term phase)
# ----------------------------------------------------------------------------
create_milestone() {
  local title="$1" desc="$2"
  local existing
  existing=$(gh api "repos/$REPO/milestones?state=all" --jq ".[] | select(.title==\"$title\") | .number" 2>/dev/null || true)
  if [[ -z "$existing" ]]; then
    gh api "repos/$REPO/milestones" -f title="$title" -f description="$desc" >/dev/null
    echo "milestone: $title"
  else
    echo "milestone exists: $title"
  fi
}

create_milestone "Phase 1 — Foundation"            "Repo, solution scaffold, EF Core, local DB, CI skeleton"
create_milestone "Phase 2 — Identity"              "Seeding, invite codes, registration + verification, blocking"
create_milestone "Phase 3 — Tenancy gate"          "Global query filter + isolation test before any Jira data is stored"
create_milestone "Phase 4 — Connection & semantic" "Connections, projects, queries, field/status mapping"
create_milestone "Phase 5 — Sync engine"           "Scheduler, incremental pull, raw landing, normalization, changelog"
create_milestone "Phase 6 — Reporting"             "View data, predefined metrics, curated gap-checks"
create_milestone "Phase 7 — Hardening"             "KMS, GDPR delete/retention, indexing, observability"
echo

# ----------------------------------------------------------------------------
# Issues
# ----------------------------------------------------------------------------
create_issue() {
  local title="$1" body="$2" labels="$3" milestone="$4"
  if gh issue list --repo "$REPO" --state all --search "in:title \"$title\"" \
       --json title --jq '.[].title' | grep -qxF "$title"; then
    echo "issue exists: $title"
    return
  fi
  gh issue create --repo "$REPO" --title "$title" --body "$body" \
     --label "$labels" --milestone "$milestone" >/dev/null
  echo "issue: $title"
}

# ---- Phase 1 — Foundation -------------------------------------------------
create_issue "Scaffold solution structure" \
"Create the layered solution:
- \`src/JiraUnpacked.Api\` — ASP.NET Core Web API (entry point)
- \`src/JiraUnpacked.Domain\` — entities + interfaces, no dependencies
- \`src/JiraUnpacked.Infrastructure\` — EF Core DbContext, migrations, Jira client (later)
- \`tests/JiraUnpacked.Tests\` — xUnit

**Done when:** solution builds and the empty test project runs green locally." \
"phase:1,type:chore,module:foundation" "Phase 1 — Foundation"

create_issue "Repo hygiene: gitignore, README, branches, protection" \
"- Add the standard .NET \`.gitignore\`
- Short README pointing at REQUIREMENTS.md
- Create \`main\` and \`develop\` branches
- Branch protection on both: require PR + passing checks

**Why:** CI deploys develop→test and main→prod, so the branches must exist before the pipeline references them." \
"phase:1,type:chore,module:foundation" "Phase 1 — Foundation"

create_issue "EF Core + Npgsql: DbContext, identity entities, first migration" \
"- Wire \`AppDbContext\` with the Npgsql provider (Postgres confirmed)
- Add identity entities from REQUIREMENTS §5: \`Users\`, \`InviteCodes\`, \`EmailVerifications\`
- Generate the first migration
- Apply against a **local** Postgres (Docker container) — no RDS yet

**Done when:** \`dotnet ef database update\` succeeds against local Postgres." \
"phase:1,type:feature,module:data" "Phase 1 — Foundation"

create_issue "CI skeleton: GitHub Actions build + test on PR" \
"One workflow triggered on pull_request: restore → build → \`dotnet test\` (xUnit).
No deploy job yet (deployment + EC2-vs-Docker is Phase 7+).

**Done when:** the workflow runs green on a test PR." \
"phase:1,type:chore,module:cicd" "Phase 1 — Foundation"

# ---- Phase 2 — Identity ----------------------------------------------------
create_issue "FR-A3 — Admin seeding via config/migration" \
"Seed the first admin(s) at deploy time from config/migration. No admin self-registration.

**Acceptance:** a seeded admin can authenticate after a fresh deploy; no public path creates an admin." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

create_issue "FR-A1 — Email-first registration + verification" \
"Order: email → username → strong password → email verification code.
- Account created in \`PendingVerification\`; code emailed
- Correct, unexpired code → \`Active\`; cannot complete otherwise
- Codes expire; resend throttled; verification rate-limited

**Acceptance:** registration cannot finish without a valid code." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

create_issue "FR-A2 — Invite-code-gated End User registration" \
"End Users register **only** with a valid invite code. No public role selector — entry path = role.

**Acceptance:** invalid/expired/used code is rejected before any account is created." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

create_issue "FR-A4 — Invite code creation (Admin)" \
"Admin generates single-use, expiring, shareable invite codes. Used/expired codes cannot be reused.

**Acceptance:** a code works exactly once and is rejected after expiry." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

create_issue "FR-A5 — Block / unblock End User (Admin)" \
"Blocking prevents authentication and pauses the user's scheduled syncs. Reversible.

**Acceptance:** a blocked user cannot log in or trigger a sync; unblock restores both." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

create_issue "FR-A6 — Strong password policy + hashing" \
"Enforce min length + complexity. Store only an Argon2/bcrypt hash; never log or return it.

**Acceptance:** weak passwords rejected; hash never appears in logs or API responses." \
"phase:2,type:feature,module:identity" "Phase 2 — Identity"

# ---- Phase 3 — Tenancy gate ------------------------------------------------
create_issue "FR-B1 — EndUserId discriminator on tenant-scoped entities" \
"Add the \`EndUserId\` tenant discriminator (and \`JiraProjectId\` where applicable) to every tenant-scoped entity." \
"phase:3,type:feature,module:tenancy" "Phase 3 — Tenancy gate"

create_issue "FR-B2 — Global EF Core query filter on EndUserId" \
"Enforce tenant filtering centrally via a global query filter resolving the current tenant — never hand-written WHERE clauses per query." \
"phase:3,type:feature,module:tenancy" "Phase 3 — Tenancy gate"

create_issue "FR-B2 — Tenant isolation integration test (THE GATE)" \
"Integration test proving tenant X cannot read tenant Y's rows through **any** read path.

**This is the gate:** must be green before any Jira data is ingested (Phase 4+). Treat the failing test as the definition of done for the tenancy slice." \
"phase:3,type:test,module:tenancy" "Phase 3 — Tenancy gate"

create_issue "FR-B2 — Postgres Row-Level Security (defense-in-depth)" \
"Add RLS policies on tenant-scoped tables as a second isolation layer beneath the EF Core filter." \
"phase:3,type:feature,module:tenancy" "Phase 3 — Tenancy gate"

# ---- Later phases as epics -------------------------------------------------
create_issue "[Epic] Phase 4 — Jira connection & semantic layer" \
"Covers FR-C1..C5: connection model (creds per instance → projects), projects, JQL queries per project, per-project sync frequency, and the field/status semantics mapping (the keystone). Break into stories when Phase 3 is green." \
"phase:4,type:epic,module:jira" "Phase 4 — Connection & semantic"

create_issue "[Epic] Phase 5 — Sync / ETL engine" \
"Covers FR-D1..D7: scheduler, incremental + idempotent pull (key on Jira issue ID), changelog history, hierarchy, raw+modeled two-layer storage, normalization by cardinality, resilience/backoff." \
"phase:5,type:epic,module:sync" "Phase 5 — Sync engine"

create_issue "[Epic] Phase 6 — Reporting & filters" \
"Covers FR-E1..E3: view ingested data, predefined metrics (counts, time-in-progress, throughput, status aging), curated gap-checks (flagship: epic-not-done-but-all-stories-done). Generic builder (FR-E4) deferred." \
"phase:6,type:epic,module:reporting" "Phase 6 — Reporting"

create_issue "[Epic] Phase 7 — Hardening" \
"KMS envelope encryption for Jira tokens, GDPR delete/retention, indexing on normalized + history tables, per-project sync observability, and the deployment pipeline (resolve EC2-direct vs Docker/ECR)." \
"phase:7,type:epic,module:security" "Phase 7 — Hardening"

echo
echo "Done. Board bootstrapped on $REPO."
