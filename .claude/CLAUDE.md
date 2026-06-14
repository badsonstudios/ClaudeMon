# ClaudeMon — Project Context

> **Read this at the start of EVERY session.** Run the `startup` skill to load
> this file, the references in `skills/startup/references/`, and verify the
> environment. The root `CLAUDE.md` imports this file so it auto-loads.

---

## Project Overview

**ClaudeMon** is a Windows system tray application (.NET 10 / WinForms) that monitors Claude AI usage for Claude Max subscribers. It polls the Anthropic API for 5-hour and 7-day rate-limit utilization, renders a color-coded tray icon, and raises desktop notifications as usage approaches configured thresholds.

Detailed, topic-by-topic context lives in `skills/startup/references/`:

| Reference | Contents |
|-----------|----------|
| `project-info.md`    | Purpose, key docs, current state |
| `tech-stack.md`      | Languages, frameworks, tools |
| `architecture.md`    | Structure, layers, patterns |
| `git-workflow.md`    | GitHub workflow, branches, commits, PRs |
| `code-style.md`      | Conventions and formatting rules |
| `testing.md`         | How to run and write tests |
| `security.md`        | Security expectations and the secrets policy |
| `api-keys-config.md` | How secrets in `.env` are named and used |

Keep those files current; this CLAUDE.md is the high-level index.

---

## Environment & Shell

- **OS:** Windows 11. Development always happens on Windows 11. WSL is available
  but generally **not** used — assume native Windows unless told otherwise.
- **Shell preference: use bash first.** Prefer **bash** (Git Bash) for scripts and
  commands. Reach for PowerShell only when bash genuinely can't do the job (e.g. a
  Windows-specific cmdlet). If you find yourself fighting bash syntax after a
  couple of tries, it's fine to switch — but bash is the default.
- Utility scripts ship in both `.sh` and `.ps1`; prefer the `.sh` version.

## Secrets & the `.env` file

All tokens, API keys, and passwords live in **`.claude/.env`**.

- **`.claude/.env` is NEVER committed** — it's in `.gitignore`. Never paste its
  contents into commits, code, logs, or chat. A PreToolUse hook
  (`.claude/hooks/block-env-staging.sh`) actively blocks `git add` of `.env`.
- **`.claude/.env.example` IS committed** — it documents every variable with
  placeholder values only.
- When you need a secret, **read it from `.claude/.env`** (use the Read tool, or
  the `get-secret` / `load-env` scripts). If a required variable is missing, ask
  the user to add it — don't hard-code a value.
- When you introduce a new secret, add a placeholder line to `.claude/.env.example`
  and tell the user to fill in the real value in `.claude/.env`.

See `skills/startup/references/api-keys-config.md` for the full variable list.

---

## Source Control — GitHub

- **Host:** GitHub, via the `gh` CLI.
- **Repo:** `badsonstudios/ClaudeMon` (already created). Confirm with the user
  before pushing or opening PRs.
- **Branches:** `main` is production-ready; create a feature/fix branch per task.
- **Commits & PRs:** descriptive messages; open PRs with `gh pr create`. Commit or
  push only when the user asks. Details in `skills/startup/references/git-workflow.md`.
- A `GITHUB_TOKEN` for API operations lives in `.claude/.env`.

---

## Working / Temporary Files

- Put scratch scripts, downloads, and throwaway test files in `.claude/work_files/`.
- Never scatter temp files in the project root. `work_files/` is git-ignored.

---

## Skills & Agents

Run skills with `/<name>`; agents are delegated to automatically for isolated work.

| Skill | Purpose |
|-------|---------|
| `/startup` | Load context + verify environment (run at session start) |
| `/pm` | Project manager — create well-formed issues & triage the backlog |
| `/implement-issue` | **Orchestrator** — ticket → plan → implement → test → review → PR (takes an issue #) |
| `/check-code` | Code-quality analysis of changed files |
| `/review` | Deeper architecture / correctness review |
| `/commit-push-pr` | Commit, push, and open a PR (asks for approval) |
| `/explain` | Explain code, a file, a subsystem, or a concept (read-only) |
| `/deep-research` | Multi-source web research with citations |

**Issue workflow:** `/pm` turns a request into GitHub issues (and triages the
backlog); `/implement-issue <n>` then drives one ticket end-to-end — fetch, plan,
**approve plan**, implement, test until green, `/review`, iterate, **approve
commit**, then `/commit-push-pr`. The two approval gates are never skipped.

**Commands** (in `.claude/commands/`): `/commit` (stage + commit, asks first),
`/pr` (push + open a PR via the `new-pr` script).

| Agent | Purpose |
|-------|---------|
| `code-reviewer` | Read-only architecture & code review |
| `debugger` | Root-cause analysis of errors and failures |
| `deep-research-agent` | Comprehensive multi-source research |

---

## Keeping Skills & Agents Up to Date

The skills in `.claude/skills/` and agents in `.claude/agents/` are **living
tooling for this project** — they start as generic templates and should evolve to
match how this codebase actually works.

**Periodically review and update them. Do this proactively, not only when asked:**

- **At session start** (during `/startup`), skim the skills/agents and flag any
  that have drifted from the current stack, structure, or conventions.
- **After any significant change** to the tech stack, architecture, build/test
  commands, or workflow, update the affected skill/agent and the relevant
  `startup/references/*.md` files so they stay accurate.
- **When you notice a repeated manual task**, capture it as a new skill, agent, or
  utility script rather than redoing it by hand each time.
- **When a skill/agent gives bad or stale guidance**, fix it at the source instead
  of just working around it once.

When you update tooling, keep the skill tables above and the references in sync,
and briefly tell the user what you changed and why.

## Utility Scripts

Reusable helper scripts live in `.claude/scripts/` (see `scripts/README.md`).
Prefer calling these over re-typing multi-step commands:

| Script | Purpose |
|--------|---------|
| `new-pr`  | Branch (if on `main`), commit, push, and open a PR via `gh` |
| `load-env` | Load `.env` into the current shell so `gh`/tools can use it |
| `get-secret` | Read a single value from `.env` without printing the whole file |

Both PowerShell (`.ps1`) and bash (`.sh`) versions are provided. When you build a
new commonly-used command, add it here as a script (both shells) and list it in
this table and in `scripts/README.md`.

## Hooks

Configured in `.claude/settings.json` (committed, so it applies everywhere):

- **block-env-staging (PreToolUse):** blocks any `git add` that would stage a
  secrets file (`.env`, `.env.local`, `.envrc`, …); `.env.example` is allowed.
  This is a safety backstop on top of `.gitignore` — requires `bash` (Git Bash on
  Windows). Script: `.claude/hooks/block-env-staging.sh`.
- **build-test-gate (Stop) — opt-in:** builds (and optionally tests) before
  finishing so build errors aren't reported as "done". Off by default; enable it
  per `.claude/hooks/README.md`. Override commands via `BUILD_CMD`/`TEST_CMD` in
  `.claude/.env`.

Add new hooks here as the project needs them.

## Other Claude Code config

- **Status line** (`.claude/settings.json` → `statusLine`): shows
  `dir | branch | model` via `.claude/scripts/statusline.sh`.
- **Output styles** (`.claude/output-styles/`): a `Concise` sample is included;
  activate with `/output-style`. (Work in progress.)
- **Template version**: this `.claude/` was scaffolded from ClaudeTemplates; the
  version is recorded in `.claude/.template-version`.

---

## Project-Specific Notes

- **Windows-only.** Targets `net10.0-windows` with WinForms (`UseWindowsForms`).
  Requires the .NET 10 Desktop Runtime to run; needs a real Windows desktop
  session (tray icon + notifications), so it can't run headless in CI for UI.
- **Build/run/test:**
  - Build: `dotnet build`
  - Run: `dotnet run --project src/ClaudeMon`
  - Test: `dotnet test` (xUnit, in `tests/ClaudeMon.Tests`)
- **Single instance:** enforced via a global mutex in `Program.cs`
  (`Global\ClaudeMon_SingleInstance`) — a second launch exits immediately.
- **Credentials:** the app reads the user's Claude Code OAuth token from
  `~/.claude/.credentials.json` (see `Services/CredentialReader.cs`). This is the
  end user's runtime credential — distinct from the developer secrets in
  `.claude/.env`. Never log or echo token contents.
- **User settings** persist as JSON via `Configuration/ConfigManager.cs`
  (separate from app source); the registry `Run` key handles "Start with Windows".
- **Tests use `InternalsVisibleTo`** (`ClaudeMon.Tests`) to reach internals.
- **Installer:** Inno Setup 6, built via `bash installer/build.sh`; output in `dist/`.
