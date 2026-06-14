#!/usr/bin/env bash
# OPT-IN Stop hook: build (and optionally test) before Claude finishes a response,
# so compile/build errors are caught instead of being reported as "done".
#
# Not enabled by default. To turn it on, add a Stop hook to .claude/settings.json
# (see .claude/hooks/README.md for the exact snippet).
#
# Behaviour:
#   - Skips when the working tree is clean (nothing changed).
#   - Auto-detects the stack's build command; override with BUILD_CMD / TEST_CMD
#     in .claude/.env (TEST_CMD is optional and only runs if set).
#   - Exit 2 blocks the stop and shows the error output to Claude. Exit 0 allows.
set -uo pipefail

dir="${CLAUDE_PROJECT_DIR:-$(pwd)}"
cd "$dir" 2>/dev/null || exit 0

# Skip if this is a git repo with no changes.
if git -C "$dir" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  [ -z "$(git -C "$dir" status --porcelain)" ] && exit 0
fi

# Optional overrides from the secrets/config file.
if [ -f "$dir/.claude/.env" ]; then
  set -a; . "$dir/.claude/.env" 2>/dev/null; set +a
fi

build_cmd="${BUILD_CMD:-}"
test_cmd="${TEST_CMD:-}"

# Auto-detect a build command if none was provided.
if [ -z "$build_cmd" ]; then
  if ls "$dir"/*.sln "$dir"/*.csproj >/dev/null 2>&1; then
    build_cmd="dotnet build --nologo -v quiet"
  elif [ -f "$dir/package.json" ] && grep -q '"build"[[:space:]]*:' "$dir/package.json"; then
    build_cmd="npm run build"
  elif [ -f "$dir/gradlew" ]; then
    build_cmd="./gradlew build -q"
  elif [ -f "$dir/Cargo.toml" ]; then
    build_cmd="cargo build -q"
  elif [ -f "$dir/go.mod" ]; then
    build_cmd="go build ./..."
  fi
fi

# Nothing to run.
[ -z "$build_cmd" ] && [ -z "$test_cmd" ] && exit 0

run() {
  local label="$1" cmd="$2"
  [ -z "$cmd" ] && return 0
  local out rc
  out="$(bash -c "$cmd" 2>&1)"; rc=$?
  if [ $rc -ne 0 ]; then
    echo "$label failed — fix these errors before finishing:" >&2
    echo "" >&2
    printf '%s\n' "$out" | tail -n 40 >&2
    return 1
  fi
  return 0
}

status=0
run "Build ($build_cmd)" "$build_cmd" || status=1
if [ $status -eq 0 ]; then
  run "Tests ($test_cmd)" "$test_cmd" || status=1
fi

[ $status -ne 0 ] && exit 2
exit 0
