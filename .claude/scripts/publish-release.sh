#!/usr/bin/env bash
# Publish a GitHub release for the current version, if one doesn't already exist.
#
#   - Version comes from src/ClaudeMon/ClaudeMon.csproj <Version> (single source of truth).
#   - Release notes are extracted from CHANGELOG.md for that version.
#   - If older CHANGELOG.md versions were never published as GitHub releases (a release got
#     skipped), their sections are rolled into this release's notes automatically, with a
#     notice — so no shipped work is ever invisible on the releases page.
#   - Attaches the built installer dist/ClaudeMon-Setup-<version>.exe when present, plus its
#     SHA-256 checksum (<installer>.sha256, sha256sum format) — the in-app auto-updater
#     refuses to run an installer it can't verify, so the checksum asset is required for
#     in-app updates to work.
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
if [ -z "$NOTES" ]; then
  echo "error: CHANGELOG.md has no section for $VERSION — add one before publishing." >&2
  echo "       (Publishing without notes creates an empty release the auto-updater will offer.)" >&2
  exit 1
fi

# Safety net: roll up older changelog versions that were never published as releases.
# Walk CHANGELOG.md versions older than this one (the file is newest-first) and collect
# their sections until we hit the first version that already has a GitHub release.
mapfile -t CL_VERSIONS < <(sed -n -E 's/^## \[([0-9]+\.[0-9]+\.[0-9]+)\].*/\1/p' "$CHANGELOG")
rollup=""
seen_current=0
for v in "${CL_VERSIONS[@]}"; do
  if [ "$v" = "$VERSION" ]; then seen_current=1; continue; fi
  [ "$seen_current" -eq 1 ] || continue
  gh release view "v$v" >/dev/null 2>&1 && break
  echo "notice: changelog version $v was never published — rolling its notes into $TAG." >&2
  section="$(awk -v ver="$v" '
    index($0, "## [" ver "]") == 1 { inblock=1; next }
    inblock && index($0, "## ") == 1 { exit }
    inblock { print }
  ' "$CHANGELOG" | sed -E '/./,$!d')"
  rollup+=$'\n'"## $v (previously unpublished)"$'\n\n'"$section"$'\n'
done
if [ -n "$rollup" ]; then
  NOTES="This release also rolls up previously unpublished changelog versions — their GitHub releases were never created; their notes are included below."$'\n\n'"$NOTES"$'\n\n'"---"$'\n'"$rollup"
fi

ASSET="$REPO_ROOT/dist/ClaudeMon-Setup-$VERSION.exe"

args=(release create "$TAG" --title "$TAG" --notes "$NOTES")
[ "$draft" -eq 1 ] && args+=(--draft)
[ -n "$target" ] && args+=(--target "$target")
if [ -f "$ASSET" ]; then
  # (Re)generate the checksum beside the installer; run from dist/ so the recorded
  # filename is bare (the updater takes the first token, but keep the file canonical).
  (cd "$(dirname "$ASSET")" && sha256sum "$(basename "$ASSET")" > "$(basename "$ASSET").sha256")
  args+=("$ASSET" "$ASSET.sha256")
else
  echo "warning: installer not found at $ASSET — publishing notes without an asset." >&2
  echo "         run 'bash installer/build.sh' first to attach the installer." >&2
fi

echo "Publishing GitHub release $TAG..."
gh "${args[@]}"
echo "Done: $TAG"
