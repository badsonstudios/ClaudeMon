# Git Workflow — ClaudeMon

**Host:** GitHub, driven by the `gh` CLI. **Repo:** `badsonstudios/ClaudeMon` (already created).

Confirm with the user before pushing or opening PRs.

## Branch strategy

- **`main`** — production-ready / integration branch (default PR base).
- **Feature branches:** `feature/<short-description>` (or
  `feature/issue-<n>-<desc>` when tracking an issue).
- **Fix branches:** `fix/<short-description>`. No `develop` branch — PRs target `main`.

## Starting work

```bash
git checkout main && git pull origin main
git checkout -b feature/<short-description>
```

## Finishing work

1. **Always ask the user for approval before committing or pushing.**
2. Stage intended files, commit with a clear message.
3. `git push -u origin <branch>`
4. `gh pr create --base main --fill`

## Commit messages

- Clear, present-tense, descriptive.
- Reference issues with `Fix #<n>:` / `Closes #<n>:` (history uses a trailing `(#<n>)`).
- Existing history has no `Co-Authored-By` trailers — follow the user's preference; ask if unsure.

## Never commit

- `.env` or any secrets, `.claude/settings.local.json`, `.claude/work_files/`.
