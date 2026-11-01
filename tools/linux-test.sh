#!/usr/bin/env bash
# Builds Core and the tests in a network-less container and runs them (upstream code is untrusted).
# Usage: tools/linux-test.sh [--game] [--package]
#   --game  additionally builds the whole solution against /mnt/d/games/Cities Skylines II (mounted read-only) and runs
#           tools/ApiCheck against the game's assemblies.
#   --package  implies --game; additionally stages the mod (tools/build.sh --package --stage-only) and zips it to
#           dist/SkylineTogetherDirect-<version>.zip. The dotnet image has neither Python nor zip, so a second network-less
#           container (docker.io/library/python:3.12-slim, `podman pull` it once) zips and runs `release_common.py check-zip`.
#           Only dist/ is mounted writable. The zip is not reproducible-by-commit: the container has no .git.
# Exit code = number of failed tests (or non-zero if a build fails); 125 = the game-layer build or the API check failed.
# Test flags mirror .github/workflows/ci.yml (--expect-at-least is read from there, --max-skipped 0).
set -u

GAME=0
PACKAGE=0
for arg in "$@"; do
  case "$arg" in
    --game) GAME=1 ;;
    --package) GAME=1; PACKAGE=1 ;;
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

PACKAGE_STEP=""
if [ "$PACKAGE" = 1 ]; then
  mkdir -p "$REPO/dist"
  MOUNTS+=(-v "$REPO/dist":/out:Z)
  PACKAGE_STEP=1
fi

podman run --rm --network=none "${SECURITY[@]}" "${MOUNTS[@]}" -e GAME_STEP="$GAME_STEP" -e PACKAGE_STEP="$PACKAGE_STEP" -e EXPECT="$EXPECT" -w /tmp "$IMAGE" bash -c '
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
if [ "$rc" = 0 ] && [ -n "${PACKAGE_STEP:-}" ]; then
  # clean first: the incremental test build above lacked GameDir, and reusing it drops the System.IO.Hashing copy
  rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj tools/ApiCheck/bin tools/ApiCheck/obj
  bash tools/build.sh --package --stage-only --game-dir /game --skip-ui-checks \
    && rm -rf /out/stage && cp -r dist/stage /out/stage \
    && sed -n "s:.*<Version>\\(.*\\)</Version>.*:\\1:p" Directory.Build.props | head -n 1 > /out/stage/VERSION \
    || { echo "PACKAGE STAGING FAILED"; rc=126; }
fi
exit $rc
'
rc=$?

if [ "$PACKAGE" = 1 ] && [ "$rc" = 0 ]; then
  VERSION="$(cat "$REPO/dist/stage/VERSION")"
  rm -f "$REPO/dist/SkylineTogetherDirect-$VERSION.zip"
  podman run --rm --network=none -v "$REPO":/src:ro,Z -v "$REPO/dist":/out:Z -w /tmp docker.io/library/python:3.12-slim sh -c '
    set -e
    rm -f /out/stage/VERSION
    python3 -B /src/tools/release_common.py zip /out/stage/SkylineTogetherDirect "/out/SkylineTogetherDirect-'"$VERSION"'.zip"
    python3 -B /src/tools/release_common.py check-zip "/out/SkylineTogetherDirect-'"$VERSION"'.zip"
    python3 -B -m zipfile -l "/out/SkylineTogetherDirect-'"$VERSION"'.zip"
    rm -rf /out/stage' || { echo "ZIP OR check-zip FAILED"; rc=127; }
  [ "$rc" = 0 ] && echo "packaged: $REPO/dist/SkylineTogetherDirect-$VERSION.zip"
fi
exit $rc
