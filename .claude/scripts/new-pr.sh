#!/usr/bin/env bash
# Branch (if needed), commit, push, and open a GitHub PR via gh.
# APPROVAL FIRST: confirm with the user before committing or pushing.
#
# Usage: ./new-pr.sh -t "Title" [-b "Body"] [-B base] [-n branch] [-a]
#   -t  PR title / commit subject (required)
#   -b  PR body / commit body
#   -B  base branch (default: main)
#   -n  branch name (default: derived from title if on base branch)
#   -a  stage all changes (git add -A) before committing
set -euo pipefail

title=""; body=""; base="main"; branch=""; stage_all=0
while getopts ":t:b:B:n:a" opt; do
  case "$opt" in
    t) title="$OPTARG" ;;
    b) body="$OPTARG" ;;
    B) base="$OPTARG" ;;
    n) branch="$OPTARG" ;;
    a) stage_all=1 ;;
    *) echo "usage: new-pr.sh -t TITLE [-b BODY] [-B base] [-n branch] [-a]" >&2; exit 2 ;;
  esac
done
[ -n "$title" ] || { echo "error: -t TITLE is required" >&2; exit 2; }

git rev-parse --is-inside-work-tree >/dev/null

current="$(git branch --show-current)"

if [ "$current" = "$base" ] || [ -z "$current" ]; then
  if [ -z "$branch" ]; then
    slug="$(printf '%s' "$title" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//' | cut -c1-50 | sed -E 's/-+$//')"
    branch="feature/$slug"
  fi
  echo "Creating branch: $branch"
  git checkout -b "$branch"
elif [ -n "$branch" ] && [ "$branch" != "$current" ]; then
  echo "Creating branch: $branch"
  git checkout -b "$branch"
else
  branch="$current"
  echo "Using current branch: $branch"
fi

[ "$stage_all" -eq 1 ] && git add -A

if [ -n "$(git diff --cached --name-only)" ]; then
  if [ -n "$body" ]; then git commit -m "$title" -m "$body"; else git commit -m "$title"; fi
else
  echo "No staged changes — skipping commit, proceeding to push/PR."
fi

git push -u origin "$branch"

if [ -n "$body" ]; then gh pr create --base "$base" --title "$title" --body "$body"
else gh pr create --base "$base" --title "$title" --fill; fi
