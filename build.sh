#!/usr/bin/env bash
# Packages self-contained OpenForge builds into dist/.
#
#   ./build.sh                      all targets, version from src/OpenForge.csproj
#   ./build.sh -v 1.2.0             override the version
#   ./build.sh linux-x64 osx-arm64  only the listed targets
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT/src/OpenForge.csproj"
DIST="$ROOT/dist"
ALL_TARGETS=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
VERSION=""
TARGETS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        -v|--version) VERSION="$2"; shift 2 ;;
        -h|--help) sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) TARGETS+=("$1"); shift ;;
    esac
done

[[ ${#TARGETS[@]} -eq 0 ]] && TARGETS=("${ALL_TARGETS[@]}")
[[ -z "$VERSION" ]] && VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"

for target in "${TARGETS[@]}"; do
    if [[ ! " ${ALL_TARGETS[*]} " =~ " $target " ]]; then
        echo "Unknown target '$target'. Choose from: ${ALL_TARGETS[*]}" >&2
        exit 1
    fi
done

command -v dotnet >/dev/null || { echo "The .NET 10 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }
mkdir -p "$DIST"

for target in "${TARGETS[@]}"; do
    name="openforge-$VERSION-$target"
    out="$DIST/$name"
    rm -rf "$out"
    echo "==> $name"

    dotnet publish "$PROJECT" \
        --configuration Release \
        --runtime "$target" \
        --self-contained true \
        --output "$out" \
        -p:Version="$VERSION" \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=none \
        -p:GenerateDocumentationFile=false \
        --nologo --verbosity quiet

    cp "$ROOT/LICENSE" "$ROOT/README.md" "$out/"

    # Linux releases carry the systemd unit and installer.
    if [[ "$target" == linux-* ]]; then
        cp "$ROOT/deploy/openforge.service" "$ROOT/deploy/install.sh" "$out/"
    fi
    find "$out" -name "*.pdb" -delete

    # Windows users expect a zip; everything else keeps the executable bit in a tarball.
    if [[ "$target" == win-* ]]; then
        rm -f "$DIST/$name.zip"
        (cd "$DIST" && zip -qr "$name.zip" "$name")
    else
        chmod +x "$out/OpenForge"
        tar -czf "$DIST/$name.tar.gz" -C "$DIST" "$name"
    fi

    rm -rf "$out"
done

(cd "$DIST" && shasum -a 256 openforge-"$VERSION"-* > "SHA256SUMS-$VERSION.txt")
echo
echo "Packages in $DIST:"
ls -1 "$DIST" | grep -- "$VERSION"
