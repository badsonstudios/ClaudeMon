# Utility Scripts

Reusable helpers for common, multi-step operations. Each comes in a PowerShell
(`.ps1`) and a bash (`.sh`) version so they work on Windows and in Git Bash / CI.

All scripts assume the secrets file lives at `.claude/.env` (one level up from
this folder). Pass an explicit path as the last argument to override.

| Script | What it does | Example |
|--------|--------------|---------|
| `new-pr` | Branch (if on base), commit staged changes, push, open a PR with `gh` | `./new-pr.sh -t "Add login" -a` |
| `load-env` | Load `.env` vars into the **current** shell (must be sourced/dot-sourced) | `. ./load-env.ps1` |
| `get-secret` | Print a single value from `.env` (without dumping the whole file) | `./get-secret.sh GITHUB_TOKEN` |

## Usage notes

### new-pr
- **Get the user's approval before running this** — it commits, pushes, and opens
  a PR. The project rule is "always confirm before committing/pushing."
- PowerShell: `./new-pr.ps1 -Title "Add login" [-Body "..."] [-Base main] [-Branch feature/x] [-All]`
- bash: `./new-pr.sh -t "Add login" [-b "..."] [-B main] [-n feature/x] [-a]`
- `-All` / `-a` stages all changes first; otherwise it commits what's already staged.
- If no branch is given and you're on the base branch, it derives `feature/<slug>`
  from the title.

### load-env
- Must be **sourced** to affect your shell:
  - PowerShell: `. .\.claude\scripts\load-env.ps1`
  - bash: `source .claude/scripts/load-env.sh`
- After loading, tools like `gh` can read `GITHUB_TOKEN` from the environment.

### get-secret
- Prints exactly one value to stdout. Use it to fetch a token for a single command
  without exposing the rest of `.env`.

## Adding new scripts

When you create a new commonly-used command, add it here (both `.ps1` and `.sh`),
then list it in this table and in the "Utility Scripts" section of
`.claude/CLAUDE.md`.
