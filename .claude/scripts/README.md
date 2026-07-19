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
| `bump-version` | Set the release version (single source of truth: the `.csproj`) | `./bump-version.sh 0.2.0` |
| `publish-release` | Create the GitHub release for the current version (notes from `CHANGELOG.md`, installer + `.sha256` attached) | `./publish-release.sh` |

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

### bump-version
- Sets the release version in the **single source of truth**:
  `src/ClaudeMon/ClaudeMon.csproj` `<Version>`.
- The installer (`installer/ClaudeMon.iss`) **derives** its version from the built
  assembly at compile time, so you do **not** edit the `.iss` — just bump and rebuild
  with `bash installer/build.sh`.
- PowerShell: `.\bump-version.ps1 0.2.0` · bash: `./bump-version.sh 0.2.0`
- Validates the `X.Y.Z` form and prints the old → new version.

### publish-release
- **Get the user's approval first** — it publishes a public GitHub release.
- **Run it after the version PR is merged to `main`**, so the tag points at merged code.
- Reads the version from `src/ClaudeMon/ClaudeMon.csproj`, extracts that version's notes
  from `CHANGELOG.md`, tags `v<version>`, and attaches `dist/ClaudeMon-Setup-<version>.exe`
  plus its generated `.sha256` checksum (run `bash installer/build.sh` first to produce the
  installer). The checksum asset is **required** for the in-app auto-updater — it refuses to
  run an installer it can't verify and falls back to opening the release page.
- **Idempotent**: if a release for the version already exists, it does nothing.
- **Rolls up skipped versions**: if older `CHANGELOG.md` versions were never published as
  GitHub releases (a publish step got missed), their sections are automatically included in
  this release's notes with a "previously unpublished" header, so no shipped work is
  invisible on the releases page. It walks the changelog newest-first and stops at the
  first version that already has a release.
- **Refuses to publish without notes**: if `CHANGELOG.md` has no section for the current
  version, the script errors out instead of creating an empty release (which the in-app
  auto-updater would offer to users). Add the changelog entry first.
- PowerShell: `.\publish-release.ps1 [-Target main] [-Draft]` · bash: `./publish-release.sh [--target main] [--draft]`
- Release flow: `bump-version` → commit/push/PR → merge → `bash installer/build.sh` → `publish-release`.

## Adding new scripts

When you create a new commonly-used command, add it here (both `.ps1` and `.sh`),
then list it in this table and in the "Utility Scripts" section of
`.claude/CLAUDE.md`.
