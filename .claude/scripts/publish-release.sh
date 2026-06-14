#!/usr/bin/env bash
# Publish a GitHub release for the current version, if one doesn't already exist.
#
#   - Version comes from src/ClaudeMon/ClaudeMon.csproj <Version> (single source of truth).
#   - Release notes are extracted from CHANGELOG.md for that version.
#   - Attaches the built installer dist/ClaudeMon-Setup-<version>.exe when present.
#   - No-op (exit 0) if a release tagged v<version> already exists.
#
# APPROVAL FIRST: this publishes a public GitHub release — confirm with the user.
#
# Usage: ./publish-release.sh [--target <branch-or-sha>] [--draft]
#   --target  commit-ish the tag should point at (default: gh's default = repo default branch HEAD)
#   --draft   create the release as a draft
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/src/ClaudeMon/ClaudeMon.csproj"
CHANGELOG="$REPO_ROOT/CHANGELOG.md"

target=""; draft=0
while [ $# -gt 0 ]; do
  case "$1" in
    --target) target="${2:-}"; shift 2 ;;
    --draft)  draft=1; shift ;;
    *) echo "usage: publish-release.sh [--target <branch-or-sha>] [--draft]" >&2; exit 2 ;;
  esac
done

[ -f "$CSPROJ" ] || { echo "error: cannot find $CSPROJ" >&2; exit 1; }
VERSION="$(sed -n -E 's|.*<Version>([^<]+)</Version>.*|\1|p' "$CSPROJ" | head -1)"
[ -n "$VERSION" ] || { echo "error: could not read <Version> from $CSPROJ" >&2; exit 1; }
TAG="v$VERSION"

# Idempotent: skip if the release already exists.
if gh release view "$TAG" >/dev/null 2>&1; then
  echo "Release $TAG already exists — nothing to publish."
  exit 0
fi

# Extract this version's notes from CHANGELOG.md: lines between "## [<version>]" and the next "## ".
NOTES="$(awk -v ver="$VERSION" '
  index($0, "## [" ver "]") == 1 { inblock=1; next }
  inblock && index($0, "## ") == 1 { exit }
  inblock { print }
' "$CHANGELOG" | sed -E '/./,$!d')"   # strip leading blank lines
[ -n "$NOTES" ] || NOTES="Release $TAG"

ASSET="$REPO_ROOT/dist/ClaudeMon-Setup-$VERSION.exe"

args=(release create "$TAG" --title "$TAG" --notes "$NOTES")
[ "$draft" -eq 1 ] && args+=(--draft)
[ -n "$target" ] && args+=(--target "$target")
if [ -f "$ASSET" ]; then
  args+=("$ASSET")
else
  echo "warning: installer not found at $ASSET — publishing notes without an asset." >&2
  echo "         run 'bash installer/build.sh' first to attach the installer." >&2
fi

echo "Publishing GitHub release $TAG..."
gh "${args[@]}"
echo "Done: $TAG"
