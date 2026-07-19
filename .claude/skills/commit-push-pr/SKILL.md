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

## Step 6: Offer a GitHub release (only if the version changed — NEVER automatic)

If this work **bumped the version** (`src/ClaudeMon/ClaudeMon.csproj` `<Version>` —
check with `git diff origin/main -- src/ClaudeMon/ClaudeMon.csproj`), a release *can*
be published — but **publishing is never part of a pre-approved run**. A blanket
"do everything" / "full run" approval covers commit, push, PR, and merge only. After
the merge, **ask the user as its own question** whether to publish a release now;
"no" simply ends the flow (unpublished versions are fine — `publish-release` rolls up
skipped versions' notes when it eventually runs). Only on an explicit "yes":

1. Make sure `CHANGELOG.md` has an entry for the new version (that's the release-notes
   source).
2. After the PR is merged, build the installer and publish the release:

   ```bash
   git checkout main && git pull
   bash installer/build.sh          # produces dist/ClaudeMon-Setup-<version>.exe
   .claude/scripts/publish-release.sh
   ```
   ```powershell
   git checkout main; git pull
   bash installer/build.sh
   .\.claude\scripts\publish-release.ps1
   ```

`publish-release` reads the version from the `.csproj`, pulls that version's notes from
`CHANGELOG.md`, tags `v<version>` on `main`, attaches the installer, and **no-ops if a
release for that version already exists**. It publishes publicly — which is why the
explicit per-release ask above is non-negotiable. Report the release URL.

If the version did **not** change, skip this step entirely (don't ask).

## Notes

- Never commit `.env` or other secrets. Verify nothing sensitive is staged.
- A `GITHUB_TOKEN` for `gh`, if needed, is in `.env`.
