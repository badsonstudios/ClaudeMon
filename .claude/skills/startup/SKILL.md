---
name: startup
description: Initialize the session — load project context from .claude/CLAUDE.md and the startup references, check the .env/secrets setup, analyze the codebase, and verify the GitHub workflow. Run at the start of every session.
user-invocable: true
---

Initialize the development session for **ClaudeMon**.

## Step 1: Load project context

Read, in order:

1. `.claude/CLAUDE.md` — high-level project context and index (required).
2. The relevant files in `.claude/skills/startup/references/` —
   `project-info.md`, `tech-stack.md`, `architecture.md`, `git-workflow.md`,
   `code-style.md`, `testing.md`, `security.md`, `api-keys-config.md`.
3. `README.md` (if present) and the dependency manifest
   (`package.json`, `*.csproj`, `build.gradle(.kts)`, `pyproject.toml`, …).

## Step 2: Check the secrets setup

- Confirm `.claude/.env.example` exists; note which variables the project expects
  (see `references/api-keys-config.md`).
- Confirm `.claude/.env` exists. If it does **not**, tell the user to copy
  `.claude/.env.example` to `.claude/.env` and fill it in. **Never print the
  contents of `.claude/.env`.**
- Confirm `.claude/.env` and `.claude/work_files/` are git-ignored. Fix
  `.gitignore` if not.

## Step 3: Check the environment

```bash
git status --short
git branch --show-current
git log --oneline -5
```

Also check the GitHub remote:

```bash
git remote -v          # is an 'origin' set?
gh repo view 2>/dev/null || echo "Not on GitHub yet — can create with: gh repo create"
```

## Step 4: Report

Provide a concise summary:

```
## Session Initialized — ClaudeMon

**Branch**: <current branch>
**Uncommitted changes**: <count or "none">
**Remote**: <origin url or "none — not on GitHub yet">
**Secrets**: <.env present / MISSING — copy from .env.example>

### Recent commits
<last 5 commits>

### Ready to go
<anything that needs attention before starting work>
```
