#!/usr/bin/env bash
# Builds Core and the tests in a network-less container and runs them (upstream code is untrusted).
# Usage: tools/linux-test.sh [--game]
#   --game  additionally builds the game layer against /mnt/d/games/Cities Skylines II (mounted read-only).
# Exit code = number of failed tests (or non-zero if a build fails); 125 = the game-layer build failed.
# Test flags mirror .github/workflows/ci.yml (--expect-at-least is read from there, --max-skipped 0).
set -u

GAME=0
for arg in "$@"; do
  case "$arg" in
    --game) GAME=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 64 ;;
  esac
done

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXPECT="$(sed -n "s/.*--expect-at-least \([0-9][0-9]*\).*/\1/p" "$REPO/.github/workflows/ci.yml" | head -1)"
[ -n "$EXPECT" ] || { echo "no --expect-at-least in ci.yml" >&2; exit 65; }
IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"

MOUNTS=(-v "$REPO":/src:ro,Z)
GAME_STEP=""
if [ "$GAME" = 1 ]; then
  if [ ! -d "/mnt/d/games/Cities Skylines II" ]; then
    echo "game dir missing: mount read-only first: sudo mount -t ntfs3 -o ro /dev/nvme0n1p2 /mnt/d" >&2
    exit 66
  fi
  MOUNTS+=(-v /mnt/d:/game:ro)
  GAME_STEP='dotnet build SkylineTogether.sln -c Release -p:DeployMod=false "-p:GameDir=/game/games/Cities Skylines II" '
fi

exec podman run --rm --network=none "${MOUNTS[@]}" -e GAME_STEP="$GAME_STEP" -e EXPECT="$EXPECT" -w /tmp "$IMAGE" bash -c '
set -u
mkdir -p /tmp/w
cd /src && tar --exclude=./.git --exclude="*/bin" --exclude="*/obj" -cf - . | tar -xf - -C /tmp/w
cd /tmp/w || exit 1
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build src/SkylineTogether.Core/SkylineTogether.Core.csproj -c Release || exit 1
dotnet build tests/SkylineTogether.Core.Tests/SkylineTogether.Core.Tests.csproj -c Release || exit 1
dotnet tests/SkylineTogether.Core.Tests/bin/Release/net8.0/SkylineTogether.Core.Tests.dll --expect-at-least "$EXPECT" --max-skipped 0
rc=$?
if [ -n "$GAME_STEP" ]; then
  eval "$GAME_STEP" || { echo "GAME-LAYER BUILD FAILED"; rc=125; }
fi
exit $rc
'
