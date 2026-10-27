#!/usr/bin/env bash
# Builds the Jellyfin plugin into out/jellyfin/XtreamIptv_<version>/ (DLL + meta.json) and zips it as
# out/jellyfin/xtream-iptv-jellyfin_<version>.zip, the layout Jellyfin's plugin repositories serve.
# Usage: ./build-jellyfin.sh [version]. Uses the local dotnet SDK when present, otherwise the container.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(tr -d ' \n' < VERSION)}"
# Jellyfin compares four-part versions.
case "$VERSION" in *.*.*.*) FULL="$VERSION" ;; *) FULL="$VERSION.0" ;; esac
ABI="${JELLYFIN_ABI:-12.4.0.0}"
OUT="out/jellyfin"
rm -rf "$OUT"
if command -v dotnet >/dev/null 2>&1; then
  dotnet publish Jellyfin.Plugin.XtreamIptv/Jellyfin.Plugin.XtreamIptv.csproj -c Release -o "$OUT/build" --no-self-contained -nologo -v q -p:Version="$VERSION"
else
  docker run --rm -v "$PWD":/src -w /src/Jellyfin.Plugin.XtreamIptv mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish -c Release -o "/src/$OUT/build" --no-self-contained -nologo -v q -p:Version="$VERSION"
fi
DIR="$OUT/XtreamIptv_$FULL"
mkdir -p "$DIR"
cp "$OUT/build/Jellyfin.Plugin.XtreamIptv.dll" "$DIR/"
cat > "$DIR/meta.json" <<EOF
{
  "guid": "c02587d5-61e0-415a-a2d5-3ccd3107d112",
  "name": "Xtream IPTV",
  "description": "Live TV with guide plus a movie and series catalogue from an Xtream account, with metadata from the Xtream lists.",
  "overview": "Xtream account as Live TV and a file-less catalogue",
  "owner": "tommasomeli",
  "category": "Live TV",
  "version": "$FULL",
  "targetAbi": "$ABI",
  "framework": "net10.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "changelog": "",
  "assemblies": ["Jellyfin.Plugin.XtreamIptv.dll"],
  "status": "Active",
  "autoUpdate": true,
  "imagePath": ""
}
EOF
rm -rf "$OUT/build"
(cd "$DIR" && zip -q -j "../xtream-iptv-jellyfin_$FULL.zip" Jellyfin.Plugin.XtreamIptv.dll meta.json)
echo "built $DIR and $OUT/xtream-iptv-jellyfin_$FULL.zip (abi $ABI)"
