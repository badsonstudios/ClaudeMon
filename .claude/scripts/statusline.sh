#!/usr/bin/env bash
# Status line for Claude Code. Configured in .claude/settings.json:
#   "statusLine": { "type": "command", "command": "bash .claude/scripts/statusline.sh" }
# Receives session JSON on stdin; prints a single line: dir | branch | model.
input="$(cat)"

model="$(printf '%s' "$input" \
  | grep -o '"display_name"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n1 \
  | sed 's/.*:[[:space:]]*"//; s/"$//')"

dir="$(basename "$(pwd)")"
branch="$(git branch --show-current 2>/dev/null)"

line="📁 $dir"
[ -n "$branch" ] && line="$line | ⎇ $branch"
[ -n "$model" ]  && line="$line | ✦ $model"
printf '%s' "$line"
