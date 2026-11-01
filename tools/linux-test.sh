#!/usr/bin/env bash
# Builds Core and the tests in a network-less container and runs them (upstream code is untrusted).
# Usage: tools/linux-test.sh [--game]
#   --game  additionally builds the whole solution against /mnt/d/games/Cities Skylines II (mounted read-only) and runs
#           tools/ApiCheck against the game's assemblies.
# Exit code = number of failed tests (or non-zero if a build fails); 125 = the game-layer build or the API check failed.
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
SECURITY=()
GAME_STEP=""
if [ "$GAME" = 1 ]; then
  if [ ! -d "/mnt/d/games/Cities Skylines II" ]; then
    echo "game dir missing: mount read-only first: sudo mount -t ntfs3 -o ro /dev/nvme0n1p2 /mnt/d" >&2
    exit 66
  fi
  # the game folder itself at a path without spaces; the read-only NTFS mount cannot be relabelled for SELinux
  # (":Z" fails, and without a label the container gets "Permission denied", so Core silently fell back to
  # netstandard2.1 and the game project failed with NU1201): the label separation is switched off for this run instead
  MOUNTS+=(-v "/mnt/d/games/Cities Skylines II":/game:ro)
  SECURITY=(--security-opt label=disable)
  GAME_STEP='dotnet build SkylineTogether.sln -c Release -p:DeployMod=false -p:GameDir=/game \
    && dotnet build tools/ApiCheck/ApiCheck.csproj -c Release -nologo -v q \
    && dotnet tools/ApiCheck/bin/Release/net8.0/ApiCheck.dll --list-types src/SkylineTogether/bin/Release/net472/SkylineTogether.dll --out /tmp/mod-types.txt \
    && { cmp -s /tmp/mod-types.txt src/SkylineTogether/Data/mod-types.generated.txt || echo "NOTE: src/SkylineTogether/Data/mod-types.generated.txt is out of date (types were added, renamed or removed); regenerate it with tools/build.sh"; } \
    && dotnet tools/ApiCheck/bin/Release/net8.0/ApiCheck.dll --managed /game/Cities2_Data/Managed --dll src/SkylineTogether/bin/Release/net472/SkylineTogether.dll --dll src/SkylineTogether/bin/Release/net472/SkylineTogether.Core.dll'
fi

exec podman run --rm --network=none "${SECURITY[@]}" "${MOUNTS[@]}" -e GAME_STEP="$GAME_STEP" -e EXPECT="$EXPECT" -w /tmp "$IMAGE" bash -c '
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
  eval "$GAME_STEP" || { echo "GAME-LAYER BUILD OR API CHECK FAILED"; rc=125; }
fi
exit $rc
'
