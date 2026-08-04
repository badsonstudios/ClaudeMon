---
name: orchestrate
description: Run the open issue queue in parallel — analyze the queue, dispatch Opus workers into isolated git worktrees, own the run log as its single writer, and keep dispatching until the queue is empty or blocked. The orchestrator NEVER implements; workers do all the work, and every PR queues for the user rather than being auto-merged. Supersedes running /implement-issue one ticket at a time.
user-invocable: true
---

Run the issue queue in parallel, continuously, with this session dispatching
and Opus workers implementing.

**Argument (optional):** issue numbers to prioritize (`/orchestrate 110 111`)
or extra notes — `$ARGUMENTS`. No argument means **every open issue that is
ready to work**.

---

## Roles — the one rule that defines this skill

- **The orchestrator (this session) NEVER writes product code.** It analyzes,
  dispatches, reviews handoffs, bookkeeps, and reports.
- **All implementation is done by Opus workers** — `Agent` tool,
  `subagent_type: general-purpose`, **`model: "opus"` on every dispatch**.
  Code review happens inside the worker, so it is an Opus review.
- **The orchestrator is the ONLY writer of the run log.** Workers never touch
  it; they report through handoff files (below). This keeps the run resumable
  if the session dies.

Everything not overridden here follows the existing project workflow:
`/implement-issue`'s steps, `.claude/CLAUDE.md`, and the references in
`.claude/skills/startup/references/`.

## Hard boundaries (always)

- **Never merge a PR.** ClaudeMon is a user-facing tray app — essentially every
  change is user-visible, so *every* PR queues for the user. This is how
  `/implement-issue`'s commit-approval gate survives being parallelized.
- **Never publish a release.** Not even after a merge, not even if the version
  was bumped. Releases are asked about separately, every time, and "no" ends it.
- **Never commit red.** A worker pushes only after its local gate is green.
- **Never touch `.claude/.env`** or put secrets anywhere git-tracked.
- **Never bypass the two-gate spirit:** the plan gate becomes a worker
  self-check that STOPS on ambiguity (below); the commit gate becomes "the user
  merges." Neither is silently dropped.
- Nothing outward-facing beyond opening PRs.

## Setup (once per run)

1. Load context as `/startup` does: `.claude/CLAUDE.md`, the
   `skills/startup/references/*.md` files, and `gh issue list --state open`.
   **This repo has no milestones** — the queue is simply the open issues, or
   the numbers passed as arguments.
2. **Check the main checkout for in-flight work** (`git status`,
   `git branch --show-current`, `git log main..HEAD`). Uncommitted or unmerged
   work — including *untracked* files that clearly belong to an open issue —
   means **ask the user how to resolve it before dispatching anything**. Fold
   it in, commit it, or park it. Never clobber it.
   *(Known at time of writing: `.config/dotnet-tools.json` is untracked work
   for #102, on branch `feature/102-ci-code-coverage`.)*
3. **`git fetch origin` and branch from `origin/main`, not local main.** A
   collaborator lands PRs on `main` mid-session; re-fetch before every dispatch
   and before every merge-readiness check.
4. **Worktree pool.** Up to 3 long-lived worktrees as siblings of the repo:
   `C:\Projects\cm-wt-1`, `cm-wt-2`, `cm-wt-3`.
   - Create on first use: `git worktree add C:\Projects\cm-wt-<n> -b <branch>
     origin/main`. Setup is just `dotnet restore` — seconds, not minutes.
   - Reuse between issues: verify `git status` is clean, then
     `git fetch && git checkout -b <new-branch> origin/main`. A dirty worktree
     from a dead worker gets stashed to a rescue branch
     (`rescue/<date>-<issue>`) before reuse, and noted in the run log.
   - Create lazily — only as many as the current wave needs.
   - If the worktree paths aren't in the session's allowed directories yet,
     say so up front rather than hitting a permission prompt mid-dispatch.
5. **Run log:** `.claude/work_files/orchestrator/RUN.md` (git-ignored). Write
   the orchestration block: run started (date), active workers
   (issue → worktree → branch), PR queue awaiting the user, blockers, and the
   single-writer rule stated explicitly. Update it on every dispatch,
   completion, and blocker — **it is the resume mechanism.**
6. Handoff directory: `.claude/work_files/orchestrator/` in the **main
   checkout** — one fixed place regardless of which worktree a worker is in.

## Queue analysis — what runs in parallel

1. **Dependency edges** from the issue bodies (they state them explicitly —
   e.g. #113 says it must land after #110).
2. **File-collision analysis: two issues that plausibly touch the same file
   never run concurrently.** This matters more here than in a large codebase —
   ClaudeMon is small, and single files carry whole features.
   Known hot files: `UI/UsageBreakdownForm.cs`, `UI/SettingsForm.cs`,
   `TrayApplication.cs`, `Services/LocalUsageStore.cs`.
   *At time of writing, #110/#111/#112/#113 ALL touch `UsageBreakdownForm.cs`
   — they are largely one serial track. What can be split off in parallel is
   the store-layer work (#112's pair query, #113's per-day series) and #113's
   new chart control, which can be built and unit-tested against the store
   before the form is wired up.*
3. **Concurrency cap: 3 workers.** Scale down on rate-limit warnings.
4. **A worktree is not a free pass to parallelize.** If splitting an issue
   across workers would mean two of them editing the same file, run it serially
   and say so — a merge conflict in a hand-laid-out WinForms file costs more
   than the parallelism saves.

If **nothing** is parallelizable, dispatch one worker on the single unblocked
item and orchestrate serially — that is still this skill's job.

## Dispatching a worker

`Agent` tool, `model: "opus"`, `run_in_background: true`, one per issue.
The prompt must contain, concretely:

- The issue number, title, and the **full acceptance criteria pasted in**.
  Workers should not re-derive scope.
- Its worktree path and branch name (`feature/<issue#>-<slug>`).
- **The worker contract:**
  1. Work ONLY in your worktree. Follow `/implement-issue` Steps 4–8.
     Instead of the plan-approval gate, self-check your plan against the
     ticket's acceptance criteria and the `startup/references/*.md`
     conventions. **If the ticket is ambiguous, contradicts the existing
     design, or the acceptance criteria can't all be met, STOP and report the
     specific question — do not guess.**
  2. **Never write the run log** (`.claude/work_files/orchestrator/RUN.md`),
     in any worktree.
  3. **Gate before push:** `dotnet build` clean (treat new warnings as
     failures) **and** `dotnet test` fully green. Tests are fast (~1s for the
     whole suite) — run them every iteration, not just at the end.
     **No machine-wide test lock is needed** — each worktree has its own
     `bin/Debug`, and the tests need no exclusive hardware. Do not invent one.
  4. **If you must run the app** (a UI change you need to see): each worktree
     builds to its own `bin/Debug`, so builds never collide — but the app
     enforces a **global single-instance mutex**, so only one instance can run
     at a time across all worktrees. Do not kill an instance you did not
     start; if one is already running, report that you could not do the visual
     check rather than fighting over it.
  5. Review your own diff against `/review`'s standards (you are Opus; the
     review is yours) — fix Blockers/Should-fixes, ~3 rounds cap.
  6. **Docs are part of done** for anything user-facing: update `README.md`
     and add a `CHANGELOG.md` entry under the current version. A missing
     changelog entry is a failing gate. If a screenshot is needed and you
     can't produce it, say so in the handoff — do not skip it silently.
  7. Push and open a **draft PR**: title `Closes #<n>: <title>`, body with the
     plain-English "what this does", how it was tested (exact test counts),
     and anything you could NOT verify. Never overstate verification.
  8. Write your handoff to `.claude/work_files/orchestrator/<issue#>.md` in
     the **main checkout**: status (done/blocked/question), PR URL, gate
     results, what you couldn't verify, anything discovered out of scope
     (report, don't fix), and any convention divergence.
  9. Your final agent message: a 10-line summary of the same.

**Mid-flight discoveries:** a worker that finds an unrelated bug reports it in
the handoff; the orchestrator files it (`gh issue create`) and queues it. No
scope creep on open PRs.

## While workers run — the orchestrator loop

On each worker completion notification:

1. Read the handoff file and the agent's summary. Update the run log: the
   orchestration block plus a one-line outcome entry with the PR link.
2. **Blocked or questioning worker:** if the question is the user's, surface it
   and move on to other tracks; if every remaining track depends on it, stop
   and report. Use `SendMessage` to continue a worker whose question you can
   answer from the repo conventions.
3. **PR queue — the orchestrator does NOT merge.** For each finished PR:
   confirm CI ("Build & test") is green, mark it ready for review, and add it
   to the user's queue in the run log with what to hand-test. Then stop.
   The user merges.
4. **Dispatch the next unblocked issue** into the freed worktree. Re-run the
   queue analysis first — the user's merges change what is unblocked, and
   `origin/main` may have moved.
5. Between notifications, schedule a fallback wakeup (20–30 min) so a hung
   worker can't stall the run silently. A worker silent past ~90 min gets
   checked (`TaskOutput`), then killed and its worktree rescued if wedged.

## Version and changelog bookkeeping (the orchestrator owns this)

Workers each add a `CHANGELOG.md` entry, which **will conflict** when several
land in one version. The orchestrator owns the resolution:

- Decide the target version **once per run** and tell every worker to write
  under that heading. Bump it with `.claude/scripts/bump-version` if the
  current version has already been released (check `gh release list`).
- If changelog conflicts appear at rebase, that is the orchestrator's to
  untangle in the run log's queue order — not a worker's.

## Stop conditions (end the run and report)

- Queue empty, or everything left is blocked on the user.
- A blocker every remaining track depends on.
- Repeated environment breakage (CLI logged out, CI down) a debugger pass
  can't resolve.
- Rate limits exhausted — report what's parked and when to resume.

## Final report

1. Shipped: one line per item, each with its PR link — **all awaiting the
   user's merge**.
2. **The user's queue:** PRs to review, plus a combined, ordered hand-test
   list. Their time is the scarcest resource — batch it.
3. **What could not be verified** — especially anything needing a second
   monitor, a real usage history, or a visual check.
4. Blocked/skipped: why, and the exact question or action the user owes.
5. Issues filed mid-run. 6. Worktree pool state. 7. Recommended next step.
8. **Do not ask about publishing a release here.** If a release is warranted,
   say so in one line and leave it — it is a separate, explicit conversation.

## Notes

- The contract is role-based: whoever orchestrates never implements, and
  workers are always explicitly `model: "opus"`.
- `/implement-issue` remains the right tool for a single ticket with the user
  at the keyboard; this skill supersedes it only while a run is active. Do not
  run both concurrently.
- Ported from the Switchboard.ai project's `/orchestrate` (2026-08-04). The
  substantive changes: no e2e lock (this suite is fast and needs no exclusive
  hardware), no milestones, the run log replaces `PROGRESS.md`, and the
  orchestrator merges **nothing** because this app has no meaningfully
  "internal" change.
