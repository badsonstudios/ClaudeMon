#!/usr/bin/env bash
# Print a single value from the project's .env file.
# Usage: ./get-secret.sh KEY [envfile]
# Prints only the requested value — never the whole file.
set -euo pipefail

name="${1:?usage: get-secret.sh KEY [envfile]}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
envfile="${2:-$script_dir/../.env}"

[ -f "$envfile" ] || { echo "error: .env not found at: $envfile" >&2; exit 1; }

while IFS= read -r line || [ -n "$line" ]; do
  # trim leading whitespace
  line="${line#"${line%%[![:space:]]*}"}"
  case "$line" in
    ''|'#'*) continue ;;
  esac
  key="${line%%=*}"
  key="$(printf '%s' "$key" | sed 's/[[:space:]]*$//')"
  [ "$key" = "$name" ] || continue
  val="${line#*=}"
  # trim surrounding whitespace
  val="${val#"${val%%[![:space:]]*}"}"
  val="${val%"${val##*[![:space:]]}"}"
  # strip matching surrounding quotes
  if [ "${val#\"}" != "$val" ] && [ "${val%\"}" != "$val" ]; then val="${val#\"}"; val="${val%\"}";
  elif [ "${val#\'}" != "$val" ] && [ "${val%\'}" != "$val" ]; then val="${val#\'}"; val="${val%\'}"; fi
  printf '%s\n' "$val"
  exit 0
done < "$envfile"

echo "error: key '$name' not found in $envfile" >&2
exit 1
