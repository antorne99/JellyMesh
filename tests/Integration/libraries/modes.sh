#!/usr/bin/env bash
# Integration 6 of the 0.6 spec (F40): a change of library mode from the Edit form. The libraries of the new mode are
# made and scanned before the plugin's old ones go, item ids and watch state stay (F23), "none" deletes only the plugin's
# libraries and lists the manual paths, and the mode changes with the remote down (§10 row 438, D28).
# A stock remote with a film and a series library, and a consumer with the plugin.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jellymesh-probe-f40] [PORT=18970] [KEEP=1] modes.sh
set -euo pipefail
PREFIX=${PREFIX:-jellymesh-probe-f40}
PORT=${PORT:-18970}
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"
trap '[ "${KEEP:-0}" = 1 ] || down' EXIT

# install: builds the plugin in a container of this run and installs it, then restarts the server.
install() {
  local out version target
  out=$(mktemp -d)
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$WT/build.yaml")
  docker run --rm --name "$PREFIX-build" -v "$WT":/src -v jellymesh-nuget:/root/.nuget/packages -v "$out":/out -w /src \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o /out/build --nologo -v quiet >/dev/null
  jq -n --arg v "$version" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$out/meta.json"
  target=/config/plugins/JellyMesh_$version
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$out/build/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/"
  docker cp "$out/meta.json" "$PREFIX-local:$target/"
  rm -rf "$out"
  docker restart "$PREFIX-local" >/dev/null
}

REMOTE_URL="http://host.docker.internal:$((PORT + 1))"
own() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .AutomaticLibraries'; }
registry() { own | jq -c '[.Libraries[] | [.Name, .Adopted]] | sort'; }
folder() { get local /Library/VirtualFolders | jq -c --arg n "$1" '.[] | select(.Name == $n)'; }
exists() { [ -n "$(folder "$1")" ] && echo yes || echo no; }
locations() { folder "$1" | jq -c '.Locations | sort'; }
lib_names() { get local /Library/VirtualFolders | jq -c '[.[].Name] | sort'; }
wait_own() {
  local i
  for i in $(seq 1 300); do
    [ "$(own | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the libraries job did not end" >&2
  return 1
}
# edit <json fields>: the edit form with the saved fields and those given, as check or save; prints the answer.
form() { jq -nc --arg u "$REMOTE_URL" --argjson f "$1" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:""} + $f'; }
check_edit() { post local "/JellyMesh/Admin/Servers/$SERVER/Edit/Check" "$(form "$1")"; }
save_edit() { req local PUT "/JellyMesh/Admin/Servers/$SERVER" "$(form "$1")"; }
# film <title>: the id of a federated film whose path has the title.
film() { stub_item local "$1"; }
played() { get local "/UserItems/$1/UserData?userId=$ME" | jq -r .Played; }
# ids: the ids of the federated films and episodes, sorted, to compare before and after a change.
ids() { get local '/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path' | jq -c '[.Items[] | select((.Path // "") | startswith("/federata")) | "\(.Id) \(.Path | split("/") | last)"] | sort'; }

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote "Film HD" movies /media/film
library remote Telefilm tvshows /media/serie
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-media:/media"
install
sleep 3
wait_ready local
login local
ME=$(get local /Users/Me | jq -r .Id)
settings local '.FederatedFolderPath = "/federata"' >/dev/null
SERVER=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "$REMOTE_URL" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"ByType"}')" | jq -r .Id)
import_all local "$SERVER"
sync_now local
wait_own
scan local
check "per type makes a film and a series library" '[["Casa - Film",false],["Casa - Serie",false]]' "$(registry)"
TEARS=$(film "Tears of Steel")
check "the film is in the per type library" 1 "$([ -n "$TEARS" ] && echo 1 || echo 0)"
post local "/UserItems/$TEARS/UserData?userId=$ME" '{"Played":true}' >/dev/null
check "the film is marked played" true "$(played "$TEARS")"
BEFORE=$(ids)

echo "--- the check lists the change and saves nothing [row 438]"
answer=$(check_edit '{"LibraryMode":"ByRemoteLibrary"}')
check "the check answers 200" 200 "$(status)"
check "it lists the libraries the change deletes" '["Casa - Film","Casa - Serie"]' "$(echo "$answer" | jq -c '.Consequences.ModeChange.DeletedLibraries | sort')"
check "the mode is still per type" ByType "$(own | jq -r .Mode)"

echo "--- per type to per remote library: new libraries made and scanned, then the old ones deleted [row 438, F23]"
save_edit '{"LibraryMode":"ByRemoteLibrary"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the libraries of the remote libraries replace those per type" '[["Casa - Film HD",false],["Casa - Telefilm",false]]' "$(registry)"
check "the old libraries are gone" "no no" "$(exists 'Casa - Film') $(exists 'Casa - Serie')"
check "the film library has the film folder" "$(own | jq -c '[.ManualPaths[] | select(.LibraryType == "movies") | .Path] | sort')" "$(locations 'Casa - Film HD')"
check "the items keep their ids" "$BEFORE" "$(ids)"
check "the film keeps its id and stays played" "$TEARS true" "$(film "Tears of Steel") $(played "$TEARS")"

echo "--- with the remote down the mode still changes [D28]"
docker stop "$PREFIX-remote" >/dev/null
save_edit '{"LibraryMode":"ByType"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the per type libraries are back" '[["Casa - Film",false],["Casa - Serie",false]]' "$(registry)"
check "the items keep their ids" "$BEFORE" "$(ids)"
check "the film stays played" true "$(played "$TEARS")"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote

echo "--- to none: only the plugin's libraries go, an admin library stays, the paths are listed [row 438]"
library local "Mie" movies /media
save_edit '{"LibraryMode":"None"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the registry is empty" '[]' "$(registry)"
check "the plugin's libraries are deleted, the admin's stays" '["Mie"]' "$(lib_names)"
check "the state lists a film and a series path" '["movies","tvshows"]' "$(own | jq -c '[.ManualPaths[].LibraryType] | sort')"
check "the mode is none" None "$(own | jq -r .Mode)"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
