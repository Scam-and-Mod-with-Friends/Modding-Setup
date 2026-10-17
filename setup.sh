#!/usr/bin/env bash
# Scam With Your Friends - modding setup (Linux / Mac)
# Run once. Re-run after any game update.
#
#   ./setup.sh
#   ./setup.sh "/path/to/Scam With Your Friends"

set -euo pipefail

BE_VERSION="5.4.23.5"
case "$(uname -s)" in
    Darwin*) BE_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BE_VERSION}/BepInEx_macos_universal_${BE_VERSION}.zip" ;;
    *)       BE_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BE_VERSION}/BepInEx_linux_x64_${BE_VERSION}.zip" ;;
esac

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'

# ── find game ────────────────────────────────────────────────────────────────

PROBE="Scam With Your Friends_Data/Managed/mscorlib.dll"

find_game() {
    for candidate in \
        "$1" \
        "${SWYG_GAME_DIR:-}" \
        "$HOME/.local/share/Steam/steamapps/common/Scam With Your Friends" \
        "$HOME/.steam/steam/steamapps/common/Scam With Your Friends" \
        "$HOME/.steam/debian-installation/steamapps/common/Scam With Your Friends" \
        "/Applications/Steam/steamapps/common/Scam With Your Friends"
    do
        [ -z "$candidate" ] && continue
        [ -f "$candidate/$PROBE" ] && echo "$candidate" && return
    done
    echo ""
}

GAME=$(find_game "${1:-}")

if [ -z "$GAME" ]; then
    echo -e "${RED}Game not found.${NC} Pass the path as an argument or set SWYG_GAME_DIR."
    exit 1
fi

echo -e "${GREEN}Game:${NC} $GAME"

# ── BepInEx ──────────────────────────────────────────────────────────────────

if [ -f "$GAME/BepInEx/core/BepInEx.dll" ]; then
    echo "BepInEx already installed, skipping download"
else
    echo "Downloading BepInEx ${BE_VERSION}..."
    TMP=$(mktemp -d)
    trap 'rm -rf "$TMP"' EXIT
    curl -fsSL "$BE_URL" -o "$TMP/bepinex.zip"
    unzip -q "$TMP/bepinex.zip" -d "$TMP/bepinex"

    cp -r "$TMP/bepinex/BepInEx" "$GAME/BepInEx"
    [ -f "$TMP/bepinex/run_bepinex.sh" ] && cp "$TMP/bepinex/run_bepinex.sh" "$GAME/run_bepinex.sh"
    [ -f "$TMP/bepinex/doorstop_libs/libdoorstop.so" ] && \
        cp -r "$TMP/bepinex/doorstop_libs" "$GAME/doorstop_libs"
    [ -f "$TMP/bepinex/.doorstop_version" ] && cp "$TMP/bepinex/.doorstop_version" "$GAME/.doorstop_version"
    echo -e "${GREEN}+ BepInEx installed${NC}"
fi

mkdir -p "$GAME/BepInEx/plugins"

if [ -f "$GAME/run_bepinex.sh" ]; then
    chmod +x "$GAME/run_bepinex.sh"
    echo "  run_bepinex.sh is executable"
fi

# ── corlib override ──────────────────────────────────────────────────────────

OVERRIDE="$GAME/unstripped_corlib"
mkdir -p "$OVERRIDE"
SEEDED=0
for dll in "$(dirname "$0")/vendor/corlib/"*.dll; do
    name=$(basename "$dll")
    if [ ! -f "$OVERRIDE/$name" ]; then
        cp "$dll" "$OVERRIDE/$name"
        SEEDED=$((SEEDED + 1))
    fi
done
[ "$SEEDED" -gt 0 ] && echo -e "${GREEN}+ seeded $SEEDED assemblies into unstripped_corlib/${NC}" \
                     || echo "corlib override already seeded"

# ── assembly patches ─────────────────────────────────────────────────────────

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PATCHER="$SCRIPT_DIR/tools/patcher"

if command -v dotnet &>/dev/null; then
    echo "Running assembly patches..."
    dotnet run --project "$PATCHER" -- "$GAME"
elif command -v pwsh &>/dev/null; then
    echo "Running assembly patches via pwsh..."
    pwsh "$SCRIPT_DIR/tools/unstrip-awaiters.ps1"   -GameDir "$GAME"
    pwsh "$SCRIPT_DIR/tools/find-vtable-breaks.ps1" -GameDir "$GAME" -Fix
else
    echo -e "${YELLOW}Warning:${NC} dotnet and pwsh not found - skipping assembly patches."
    echo "  These are required or the game may crash on load."
    echo "  Install dotnet: https://dotnet.microsoft.com/download"
fi

# ── done ─────────────────────────────────────────────────────────────────────

echo ""
echo -e "${GREEN}Done!${NC}"
if [ -f "$GAME/run_bepinex.sh" ]; then
    echo "Launch the game with: $GAME/run_bepinex.sh"
else
    echo "Launch through Steam as normal."
fi
echo "Drop mod .dll files into: $GAME/BepInEx/plugins/"
