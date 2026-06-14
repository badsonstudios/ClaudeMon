---
name: pm
description: Project-manager skill — turn a request into well-formed GitHub issues, and triage/prioritize the existing backlog. The front door that feeds /implement-issue.
user-invocable: true
---

Act as the project manager for this repo: create good tickets and keep the
backlog in order. Work over GitHub via the `gh` CLI.

**Argument:** `$ARGUMENTS`

Pick the mode from the argument:

- A feature/bug/idea description → **Create mode**.
- `triage`, `backlog`, `prioritize`, or no argument → **Triage mode**.

If it's ambiguous, ask which the user wants.

---

## Create mode — turn a request into issues

1. **Clarify** if the request is vague (target users, scope, constraints,
   done-ness). Ask 1–3 focused questions rather than guessing.
2. **Decompose** into discrete, independently shippable issues — not one giant
   ticket. Split epics into child issues.
3. For **each** issue, draft:
   - **Title** — concise, action-oriented.
   - **Problem / why** — the user-facing need or bug.
   - **Acceptance criteria** — a checklist of testable outcomes.
   - **Scope / out-of-scope** — what this ticket does *not* cover.
   - **Labels** and a rough **size** (S/M/L).
4. **Confirm** the drafts with the user, then create them:
   ```bash
   gh issue create --title "<title>" --body "<body>" --label "<labels>"
   ```
5. Report the created issue numbers and URLs. Suggest the next step:
   `/implement-issue <n>`.

## Triage mode — order the backlog

1. List open work:
   ```bash
   gh issue list --state open --limit 100
   ```
2. Group issues by theme/area. For each, assess **priority** (impact × effort)
   and flag anything **stale**, **duplicate**, or **under-specified** (missing
   acceptance criteria).
3. Recommend an **ordered next 3–5** to work on, each with a one-line rationale.
   Propose milestones and label cleanups where useful.
4. Offer to apply changes (labels, milestones, closing dupes) — **ask before
   modifying any issue**.

---

## Notes

- Uses the `gh` CLI; a `GITHUB_TOKEN` is in `.claude/.env` if needed.
- Always confirm before **creating** or **modifying** issues.
- Hand off implementation to **`/implement-issue <n>`**.
