#!/bin/bash
# Bump the project version.
#
# Single source of truth: src/ClaudeMon/ClaudeMon.csproj <Version>. The installer
# (installer/ClaudeMon.iss) derives its version from the built assembly, so this is
# the only file that needs changing — build.sh then picks it up automatically.
#
# Usage: bump-version.sh <X.Y.Z>
#   e.g. bump-version.sh 0.2.0
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# .claude/scripts -> repo root is two levels up
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
CSPROJ="$REPO_ROOT/src/ClaudeMon/ClaudeMon.csproj"

NEW_VERSION="$1"
if [ -z "$NEW_VERSION" ]; then
    echo "Usage: bump-version.sh <X.Y.Z>  (e.g. 0.2.0)" >&2
    exit 1
fi
if ! [[ "$NEW_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Error: version must be in the form X.Y.Z (e.g. 0.2.0)" >&2
    exit 1
fi
if [ ! -f "$CSPROJ" ]; then
    echo "Error: cannot find $CSPROJ" >&2
    exit 1
fi

OLD_VERSION="$(sed -n -E 's|.*<Version>([^<]+)</Version>.*|\1|p' "$CSPROJ" | head -1)"
sed -i -E "s|<Version>[^<]+</Version>|<Version>${NEW_VERSION}</Version>|" "$CSPROJ"

echo "Bumped version: ${OLD_VERSION:-<none>} -> ${NEW_VERSION}"
echo "Updated: $CSPROJ"
echo "The installer derives its version from the built assembly — no .iss change needed."
