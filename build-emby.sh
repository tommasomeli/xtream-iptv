#!/usr/bin/env bash
# Builds the Emby plugin into out/emby/Emby.XtreamIptv.dll. Usage: ./build-emby.sh [version]
# Uses the local dotnet SDK when present, otherwise the .NET 10 SDK container.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(tr -d ' \n' < VERSION)}"
OUT="out/emby"
rm -rf "$OUT"
if command -v dotnet >/dev/null 2>&1; then
  dotnet publish Emby.XtreamIptv/Emby.XtreamIptv.csproj -c Release -o "$OUT/build" --no-self-contained -nologo -v q -p:Version="$VERSION"
else
  docker run --rm -v "$PWD":/src -w /src/Emby.XtreamIptv mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -o "/src/$OUT/build" --no-self-contained -nologo -v q -p:Version="$VERSION"
fi
cp "$OUT/build/Emby.XtreamIptv.dll" "$OUT/"
rm -rf "$OUT/build"
echo "built $OUT/Emby.XtreamIptv.dll ($VERSION)"
