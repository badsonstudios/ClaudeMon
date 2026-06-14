# API Keys & Configuration — ClaudeMon

All credentials and config secrets live in **`.claude/.env`**.
`.claude/.env` is git-ignored (and a PreToolUse hook blocks staging it);
`.claude/.env.example` (committed) is the source of truth for *which* variables exist.

## How to use secrets

1. Read `.claude/.env` with the Read tool when you need a value, **or** use the
   helper scripts in `.claude/scripts/` (they default to `.claude/.env`):
   - `get-secret <KEY>` — print one value (e.g. `./get-secret.sh GITHUB_TOKEN`).
   - `load-env` — load all vars into the shell so tools like `gh` inherit them
     (source it: `source .claude/scripts/load-env.sh` / `. .\.claude\scripts\load-env.ps1`).
2. Use the value directly in the command — do **not** print it or commit it.
3. If a needed variable is missing from `.claude/.env`, ask the user to add it.

## Variables

| Variable | Required | Purpose |
|----------|----------|---------|
| `GITHUB_TOKEN` | optional | GitHub API / `gh` operations during development |
| `GITHUB_PROJECT` | optional | Repo URL (`https://github.com/badsonstudios/ClaudeMon`); not a secret — convenience reference for tooling/scripts |

> **Note:** ClaudeMon itself needs **no secrets in `.claude/.env` to run**. At runtime it
> reads the end user's Claude Code OAuth token from `~/.claude/.credentials.json` — that is
> *not* a developer secret and does not belong in `.env`. The only `.env` value used here is
> the optional `GITHUB_TOKEN` for `gh`/CI work.

Keep this table in sync with `.claude/.env.example`. Add a row whenever you add a secret.
