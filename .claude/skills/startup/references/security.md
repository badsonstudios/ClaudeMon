# Security — ClaudeMon

## Secrets policy

- All tokens, keys, and passwords live in **`.claude/.env`**, which is
  **git-ignored and never committed** (a PreToolUse hook also blocks staging it).
- `.claude/.env.example` documents required variables with placeholders only.
- Read secrets from `.claude/.env` at use time; never hard-code them, never log
  them, never paste them into commits or chat.
- New secret? Add a placeholder to `.claude/.env.example` and ask the user to fill
  in `.claude/.env`.

## General expectations

- **End-user credential:** the app reads the Claude Code OAuth token from
  `~/.claude/.credentials.json` (`Services/CredentialReader.cs`). Treat it like a secret —
  never log it, echo it, include it in error messages, or write it anywhere on disk.
- **No telemetry / no exfiltration:** the app only talks to the Anthropic usage API. Don't
  add outbound calls that send usage data or tokens anywhere else.
- Validate/parse API responses defensively (`System.Text.Json`); handle missing/expired
  tokens and network failures gracefully (show the error icon, don't crash).
- This is a local desktop app — there's no server-side auth surface, but keep registry and
  file writes (settings, startup key) scoped to the current user.
- Keep NuGet dependencies updated; watch for known vulnerabilities.

## Review

- Run `/security-review` (or delegate to a security-focused agent) for
  security-sensitive changes — auth, data access, input handling, crypto.
