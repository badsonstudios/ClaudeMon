---
name: implement-issue
description: End-to-end orchestrator for a GitHub issue — fetch the ticket, plan, get the plan approved, implement, test until green, code review, iterate on findings, then commit/push/open a PR after you approve. Takes an issue number.
user-invocable: true
---

Drive a GitHub issue from ticket to pull request.

**Argument:** the issue number, plus any extra notes — `$ARGUMENTS`

This skill **orchestrates** other skills and agents. It has **two mandatory human
approval gates** — plan approval (Step 3) and commit approval (Step 8) — that are
**never** skipped, even if the user said "go ahead" earlier for a different step.

---

## Step 1 — Grab the ticket

```bash
gh issue view <n>
gh issue view <n> --comments
```

Read the issue body, comments, and any linked/blocking issues. Restate the
**goal** and **acceptance criteria** back in your own words. If the ticket is
ambiguous or under-specified, ask the user before planning — don't guess.

## Step 2 — Create a plan

For a non-trivial issue, delegate to the **Plan** agent to design the approach;
otherwise plan inline. The plan must be concrete:

- Files/modules to change (and why).
- The approach and any architectural trade-offs (see `references/architecture.md`).
- Tests to add or update (see `references/testing.md`).
- Risks, edge cases, and anything explicitly **out of scope**.

Keep scope tight to the ticket — don't fold in unrelated work.

## Step 3 — Approval gate #1 (plan)

**CRITICAL:** Present the plan and **wait for explicit approval**. Do not write
any implementation code before the user approves. If they request changes, revise
and re-present.

## Step 4 — Implement

- If on `main`, branch first: `git checkout -b feature/<n>-<short-slug>`.
- Implement exactly to the approved plan. Follow `references/code-style.md`.
- Keep commits-worth of work coherent; don't commit yet (that's Step 9).

## Step 5 — Test (iterate until green)

Build and run the tests per `references/testing.md`. On failure:

1. Diagnose — use the **debugger** agent for non-obvious root causes.
2. Fix.
3. Re-run.

Loop until the build is clean and tests pass. If you hit a genuine blocker you
can't resolve, stop and report it with the failing output — don't report
half-working code as "done".

## Step 6 — Code review

Run the **`/review`** skill on the diff (it delegates to the read-only
`code-reviewer` agent). Triage the findings into **Blocker / Should-fix / Nit**.

## Step 7 — Iterate

If the review surfaces **Blocker** or **Should-fix** items, address them, then go
**back to Step 5** (test) and **Step 6** (review) again. Repeat until:

- the build/tests are green, **and**
- the review has no remaining Blocker/Should-fix items (Nits may be left with a
  note, or fixed if cheap).

Cap the loop at ~3 rounds. If it isn't converging, stop and report what's left.

## Step 8 — Approval gate #2 (commit)

Summarize: what changed, test status, review outcome, files touched. **Wait for
explicit approval to commit and open a PR.**

## Step 9 — Commit, push, open the PR

Run the **`/commit-push-pr`** skill. Use a commit/PR that closes the issue
(`Closes #<n>:` in the message). Report the PR URL when done.

---

## Notes

- The two approval gates are non-negotiable. Never commit/push without Gate #2.
- Never commit `.env` or secrets (a hook also blocks staging `.env`).
- This skill is the back end of **`/pm`** — `/pm` creates the tickets,
  `/implement-issue` ships them.
