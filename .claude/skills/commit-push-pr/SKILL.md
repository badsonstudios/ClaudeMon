---
name: commit-push-pr
description: Commit the current changes, push to GitHub, and open a pull request. Always asks for explicit approval before committing or pushing.
user-invocable: true
---

Commit, push, and open a PR for the current work.

If the user provided a summary or PR title: $ARGUMENTS

## Step 1: Review what will be committed

```bash
git status
git diff
```

Summarize the changes for the user.

## Step 2: Get explicit approval

**CRITICAL: Always ask the user for approval before committing or pushing.**
Present the plan (files, branch, commit message, PR base) and wait for an explicit
"yes" — unless the user already told you in this session to commit/push without
asking again.

## Step 3: Branch (if needed)

If on `main`, create a feature/fix branch first:
`git checkout -b feature/<short-description>`.

## Step 4: Commit

- Stage the intended files (`git add ...`).
- Write a clear, descriptive commit message. Reference an issue with
  `Fix #<n>:` / `Closes #<n>:` when applicable.
- Follow the project's commit conventions in `references/git-workflow.md`.

## Step 5: Push and open the PR

After approval, the quickest path is the helper script (it branches if needed,
commits staged changes, pushes, and opens the PR):

```bash
# bash
.claude/scripts/new-pr.sh -t "<title>" -b "<body>" -B <base-branch>
```
```powershell
# PowerShell
.\.claude\scripts\new-pr.ps1 -Title "<title>" -Body "<body>" -Base <base-branch>
```

Or do it by hand:

```bash
git push -u origin <branch>
gh pr create --base <base-branch> --fill
```

Use the PR base branch defined in `references/git-workflow.md` (default `main`).
Report the PR URL.

## Notes

- Never commit `.env` or other secrets. Verify nothing sensitive is staged.
- A `GITHUB_TOKEN` for `gh`, if needed, is in `.env`.
