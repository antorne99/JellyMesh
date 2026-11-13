#!/usr/bin/env bash
# Q318: what stock 12.4 does with the old CollectionFolder of a library renamed with no top folder validation.
# Two libraries with no picture: one renamed without refreshLibrary, then a scan and a restart; one renamed with it.
# PLUGIN_SRC=<checkout> installs the plugin built from it (for example 0.4.3.0); without it the server is stock.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-stock-rename] [PORT=18980] [PLUGIN_SRC=dir] [KEEP=1] stock.sh
set -euo pipefail
PREFIX=${PREFIX:-jm-stock-rename}
PORT=${PORT:-18980}
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"
trap '[ "${KEEP:-0}" = 1 ] || down' EXIT

# install <source>: builds the plugin of a checkout and installs it, then restarts the server.
install() {
  local src=$1 out version abi target
  out=$(mktemp -d)
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$src/build.yaml")
  abi=$(sed -n 's/^targetAbi: "\(.*\)"$/\1/p' "$src/build.yaml")
  docker run --rm --name "$PREFIX-build" -v "$src":/src -v jellymesh-nuget:/root/.nuget/packages -v "$out":/out -w /src \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o /out/build --nologo -v quiet >/dev/null
  jq -n --arg v "$version" --arg a "$abi" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: $a,
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$out/meta.json"
  target=/config/plugins/JellyMesh_$version
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$out/build/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/"
  docker cp "$out/meta.json" "$PREFIX-local:$target/"
  rm -rf "$out"
  docker restart "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
}
folders() { sql "select Name from BaseItems where Type like '%.CollectionFolder' order by Name;" | paste -sd, -; }
views() { get local "/UserViews?userId=$(user_id local admin | tr -d -)" | jq -r '[.Items[].Name] | sort | join(",")'; }
rename() { post local "/Library/VirtualFolders/Name?name=$1&newName=$2&refreshLibrary=$3" >/dev/null; }

echo "--- setup: $PREFIX-local on $PORT"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/uno /media/due && chmod -R a+rwX /media'
if [ -n "${PLUGIN_SRC:-}" ]; then
  install "$PLUGIN_SRC"
  echo "   plugin $(get local /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Version')"
fi
library local Uno tvshows /media/uno
library local Due tvshows /media/due
scan local
check "both libraries are in the database" "Due,Uno" "$(folders)"

restart() { docker restart "$PREFIX-local" >/dev/null; sleep 3; wait_ready local; login local; }
has() { folders | tr , '\n' | grep -cx "$1" || true; }

echo "--- Uno renamed with no validation of the top folders, then a scan"
rename Uno Uno2 false
check "rename without refresh" 204 "$(status)"
sleep 3
scan local
echo "   database $(folders), views $(views)"
check "stock keeps the row of the old name after a scan" 1 "$(has Uno)"
restart
scan local
echo "   after a restart and a scan: database $(folders), views $(views)"
check "and after a restart and a scan, among the views too" "1 1" "$(has Uno) $(views | tr , '\n' | grep -cx Uno || true)"

echo "--- a library added without refreshLibrary, which validates the top folders once and does not scan"
library local Tre tvshows /media/uno
echo "   database $(folders), views $(views)"
check "that one validation drops the old Uno row" 0 "$(has Uno)"
check "but the views still list it until the next validation" 1 "$(views | tr , '\n' | grep -cx Uno || true)"

echo "--- Due renamed with refreshLibrary, which validates the top folders before its scan"
rename Due Due2 true
check "rename with refresh" 204 "$(status)"
sleep 3
scan local
echo "   database $(folders), views $(views)"
check "the library renamed with refresh leaves no row" 0 "$(has Due)"
exit "$FAILED"
