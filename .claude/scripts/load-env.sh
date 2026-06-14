#!/usr/bin/env bash
# Load .env into the current shell. SOURCE this script so vars persist:
#     source .claude/scripts/load-env.sh
# After loading, child processes (e.g. gh) inherit the variables.

# Resolve script dir whether sourced from bash or zsh.
if [ -n "${BASH_SOURCE:-}" ]; then _src="${BASH_SOURCE[0]}"; else _src="$0"; fi
_dir="$(cd "$(dirname "$_src")" && pwd)"
_envfile="${1:-$_dir/../.env}"

if [ ! -f "$_envfile" ]; then
  echo "warning: .env not found at: $_envfile" >&2
else
  _count=0
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line#"${line%%[![:space:]]*}"}"
    case "$line" in ''|'#'*) continue ;; esac
    key="${line%%=*}"; key="$(printf '%s' "$key" | sed 's/[[:space:]]*$//')"
    val="${line#*=}"
    val="${val#"${val%%[![:space:]]*}"}"; val="${val%"${val##*[![:space:]]}"}"
    if [ "${val#\"}" != "$val" ] && [ "${val%\"}" != "$val" ]; then val="${val#\"}"; val="${val%\"}";
    elif [ "${val#\'}" != "$val" ] && [ "${val%\'}" != "$val" ]; then val="${val#\'}"; val="${val%\'}"; fi
    [ -z "$val" ] && continue   # skip empty placeholders
    export "$key=$val"
    _count=$((_count + 1))
  done < "$_envfile"
  echo "Loaded $_count variable(s) from $_envfile"
fi
unset _src _dir _envfile _count line key val 2>/dev/null || true
