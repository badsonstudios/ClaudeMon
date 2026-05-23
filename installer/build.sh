#!/bin/bash
# Build and publish ClaudeMon, then create the installer
# Requires: .NET 10 SDK, Inno Setup 6 (iscc in PATH or at default location)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
PUBLISH_DIR="$SCRIPT_DIR/../publish"

echo "=== Publishing ClaudeMon ==="
dotnet publish "$PROJECT_DIR/src/ClaudeMon/ClaudeMon.csproj" \
    -c Release \
    -r win-x64 \
    --self-contained false \
    -o "$PUBLISH_DIR"

echo ""
echo "=== Published to: $PUBLISH_DIR ==="
echo ""

# Try to find Inno Setup compiler
ISCC=""
if command -v iscc &> /dev/null; then
    ISCC="iscc"
elif [ -f "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" ]; then
    ISCC="/c/Program Files (x86)/Inno Setup 6/ISCC.exe"
elif [ -f "/c/Program Files/Inno Setup 6/ISCC.exe" ]; then
    ISCC="/c/Program Files/Inno Setup 6/ISCC.exe"
fi

if [ -n "$ISCC" ]; then
    echo "=== Building Installer ==="
    "$ISCC" "$SCRIPT_DIR/ClaudeMon.iss"
    echo ""
    echo "=== Installer created in: $PROJECT_DIR/dist/ ==="
else
    echo "Inno Setup not found. Install from https://jrsoftware.org/isdownload.php"
    echo "Published files are ready in: $PUBLISH_DIR"
fi
