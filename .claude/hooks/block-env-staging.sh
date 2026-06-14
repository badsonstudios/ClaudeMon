#!/usr/bin/env bash
# PreToolUse hook — blocks staging the secrets file `.claude/.env` (or any
# dotenv variant) with `git add`. Secrets must never be committed.
#
# Receives the tool call as JSON on stdin. Exit 2 = block (stderr is shown to
# Claude). Exit 0 = allow. `.env.example` is always allowed.

payload="$(cat)"

# Best-effort extraction of the Bash command; fall back to the whole payload.
cmd="$(printf '%s' "$payload" \
  | grep -o '"command"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n1 \
  | sed 's/.*"command"[[:space:]]*:[[:space:]]*"//; s/"$//')"
[ -z "$cmd" ] && cmd="$payload"

# Only inspect `git add` invocations.
printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+add' || exit 0

block() {
  echo "BLOCKED: refusing to stage a secrets file." >&2
  echo "$1" >&2
  echo "Secrets live in .claude/.env and must never be committed." >&2
  echo "If you meant the committed template, stage '.claude/.env.example' instead." >&2
  exit 2
}

# 1) Explicit reference to a dotenv file as a path argument.
#    Word-split safely (globbing off) and check each token's basename.
set -f
for raw in $cmd; do
  w="${raw%\"}"; w="${w#\"}"; w="${w%\'}"; w="${w#\'}"   # strip surrounding quotes
  base="${w##*/}"                                        # basename
  case "$base" in
    .env.example) continue ;;                            # always allowed
    .env|.env.*|.envrc)
      block "Command stages secrets file '$w'." ;;
  esac
done
set +f

# 2) Broad add (git add . / -A / --all / -u / *): allow only if .env is ignored.
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+add[[:space:]]+(-A\b|--all\b|-u\b|\.([[:space:]]|$)|\*)'; then
  dir="${CLAUDE_PROJECT_DIR:-.}"
  if [ -f "$dir/.claude/.env" ] && ! git -C "$dir" check-ignore -q .claude/.env 2>/dev/null; then
    block "A broad 'git add' would stage '.claude/.env' because it is not git-ignored. Add it to .gitignore first."
  fi
  if [ -f "$dir/.env" ] && ! git -C "$dir" check-ignore -q .env 2>/dev/null; then
    block "A broad 'git add' would stage '.env' because it is not git-ignored. Add it to .gitignore first."
  fi
fi

exit 0
