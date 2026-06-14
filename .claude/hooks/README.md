# Hooks

Hooks run automatically at lifecycle points. They're configured in
`.claude/settings.json` (committed, so they apply to everyone on the project).

## Active by default

### `block-env-staging.sh` — PreToolUse
Blocks any `git add` that would stage a secrets file (`.env`, `.env.local`,
`.envrc`, …); `.env.example` is allowed. A safety backstop on top of `.gitignore`.
Requires `bash` (Git Bash on Windows). Already wired in `settings.json`.

## Opt-in

### `build-test-gate.sh` — Stop
Builds (and optionally tests) before Claude finishes a response, so build errors
are caught instead of being reported as done. **Not enabled by default.**

- Skips when the working tree is clean.
- Auto-detects the build command (.NET / npm / Gradle / Cargo / Go).
- Override with `BUILD_CMD` / `TEST_CMD` in `.claude/.env` (e.g.
  `BUILD_CMD=dotnet build`, `TEST_CMD=dotnet test`). `TEST_CMD` only runs if set.

**To enable**, add a `Stop` entry to the `hooks` object in `.claude/settings.json`:

```json
"Stop": [
  {
    "hooks": [
      {
        "type": "command",
        "command": "bash \"$CLAUDE_PROJECT_DIR/.claude/hooks/build-test-gate.sh\"",
        "timeout": 120
      }
    ]
  }
]
```

## Adding hooks

Drop a script here, wire it in `settings.json`, and document it above. Exit code
`2` from a hook blocks the action and feeds stderr back to Claude.
