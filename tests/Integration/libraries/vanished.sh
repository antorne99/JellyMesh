#!/usr/bin/env bash
# Integration 10 of the 0.6 spec (F38): a remote library absent from two consecutive correct answers leaves the imported
# ones, its folder empties and the library the plugin made for it is deleted after the stubs; an adopted library only loses
# its paths [D22]; with the remote off nothing is removed (§10 row 436). The deleted library leaves the views of a user with
# all folders. A stock remote with a film, a series and an empty library.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jellymesh-probe-f38] [PORT=19010] [KEEP=1] vanished.sh
set -euo pipefail
PREFIX=${PREFIX:-jellymesh-probe-f38}
PORT=${PORT:-19010}
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

own() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .AutomaticLibraries'; }
registry() { own | jq -c '[.Libraries[] | [.Name, .Adopted]] | sort'; }
vanished() { own | jq -c '.Vanished | sort'; }
imported() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .ImportedLibraryIds | sort'; }
folder() { get local /Library/VirtualFolders | jq -c --arg n "$1" '.[] | select(.Name == $n)'; }
exists() { [ -n "$(folder "$1")" ] && echo yes || echo no; }
in_views() { get local "/UserViews?userId=$(user_id local admin)" | jq -r --arg n "$1" 'if any(.Items[]; .Name == $n) then "yes" else "no" end'; }
in_media_folders() { get local /Library/MediaFolders | jq -r --arg n "$1" 'if any(.Items[]; .Name == $n) then "yes" else "no" end'; }
locations() { folder "$1" | jq -c '.Locations | sort'; }
remote_id() { get remote /Library/VirtualFolders | jq -r --arg n "$1" '.[] | select(.Name == $n) | .ItemId'; }
on_disk() { docker exec "$PREFIX-local" sh -c '[ -d "$1" ] && echo yes || echo no' sh "$FOLDER/$1"; }
strms() { docker exec "$PREFIX-local" sh -c 'find "$1" -name "*.strm" 2>/dev/null | wc -l | tr -d " "' sh "$FOLDER/$1"; }
# form <json>: the edit form of the server with those fields changed.
form() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" --argjson c "$1" '.Servers[] | select(.Id == $id) | {Name, Url, Username: "", Password: ""} + $c'; }
switch() { # switch <mode>: saves the library mode with the edit form, and prints the status
  req local PUT "/JellyMesh/Admin/Servers/$SERVER" "$(form "{\"LibraryMode\":\"$1\"}")" >/dev/null
  status
}
wait_own() {
  local i
  for i in $(seq 1 300); do
    [ "$(own | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the libraries job did not end" >&2
  return 1
}
# Removes a remote library as the Dashboard does: with refreshLibrary=false Jellyfin 12.4 keeps it in the user views,
# with no items, even after a scan or a restart.
remove_remote() {
  local i u
  req remote DELETE "/Library/VirtualFolders?name=$(jq -rn --arg v "$1" '$v | @uri')&refreshLibrary=true" >/dev/null
  u=$(user_id remote jellymesh)
  for i in $(seq 1 120); do
    get remote "/UserViews?userId=$u" | jq -e --arg n "$1" 'all(.Items[]; .Name != $n)' >/dev/null && return 0
    sleep 1
  done
  echo "the remote still lists $1" >&2
  return 1
}
make() { # make <name> <type> <json array of paths>: an admin library with those paths.
  # shellcheck disable=SC2046
  library local "$1" "$2" $(echo "$3" | jq -r 'map(@uri) | .[]')
}

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
scan remote
FILM_ID=$(remote_id Film)
SERIE_ID=$(remote_id Serie)
BOTH=$(jq -cn --arg f "$FILM_ID" --arg s "$SERIE_ID" '[$f, $s] | sort')

start local "$PREFIX-media:/media"
install
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
SERVER=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$((PORT + 1))" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"None"}')" | jq -r .Id)
FOLDER=$(server_folder "$SERVER")
import_all local "$SERVER"
# An empty library not imported keeps the list of the remote non-empty once Film and Serie are gone.
docker exec "$PREFIX-remote" mkdir -p /cache/altro
library remote Altro movies /cache/altro
scan remote
sync_now local
FILM_PATHS=$(own | jq -c '[.ManualPaths[] | select(.LibraryType == "movies") | .Path] | sort')
make "Film miei" movies "$FILM_PATHS"
check "switching on answers 200" 200 "$(switch ByType)"
wait_own
scan local
check "the film library is adopted and the series one made" '[["Casa - Serie",false],["Film miei",true]]' "$(registry)"
check "both remote libraries hold titles" "yes yes" "$([ "$(strms "$FILM_ID")" -gt 0 ] && echo yes || echo no) $([ "$(strms "$SERIE_ID")" -gt 0 ] && echo yes || echo no)"
SERIE_STRMS=$(strms "$SERIE_ID")

echo "--- with the remote off nothing is removed"
docker stop "$PREFIX-remote" >/dev/null
sync_now local
sync_now local
check "both libraries stay imported" "$BOTH" "$(imported)"
check "the series library stays" yes "$(exists 'Casa - Serie')"
check "its titles stay" "$SERIE_STRMS" "$(strms "$SERIE_ID")"
check "no warning" '[]' "$(vanished)"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
login remote

echo "--- a remote library absent from one answer stays"
remove_remote Serie
sync_now local
check "the series library stays imported" "$BOTH" "$(imported)"
check "the series library stays" yes "$(exists 'Casa - Serie')"
check "its titles stay" "$SERIE_STRMS" "$(strms "$SERIE_ID")"

echo "--- the second absence in a row drops it, empties its folder and deletes the library the plugin made [row 436]"
sync_now local
for i in $(seq 1 120); do
  [ "$(exists 'Casa - Serie')" = no ] && [ "$(on_disk "$SERIE_ID")" = no ] && [ "$(in_views 'Casa - Serie')" = no ] \
    && [ "$(in_media_folders 'Casa - Serie')" = no ] && break
  sleep 1
done
check "only the film library stays imported" "[\"$FILM_ID\"]" "$(imported)"
check "the stubs of the series are gone" 0 "$(strms "$SERIE_ID")"
check "the library the plugin made is deleted" no "$(exists 'Casa - Serie')"
check "it leaves the views of a user with all folders" no "$(in_views 'Casa - Serie')"
check "it leaves the media folders" no "$(in_media_folders 'Casa - Serie')"
check "the folder of the remote library is deleted" no "$(on_disk "$SERIE_ID")"
check "the page warns" '["Serie"]' "$(vanished)"
check "the film titles stay" yes "$([ "$(strms "$FILM_ID")" -gt 0 ] && echo yes || echo no)"
check "the adopted library is untouched" "$FILM_PATHS" "$(locations 'Film miei')"

echo "--- an adopted library only loses the paths of a vanished remote library [D22]"
remove_remote Film
sync_now local
sync_now local
for i in $(seq 1 120); do
  [ "$(locations 'Film miei')" = '[]' ] && break
  sleep 1
done
check "the adopted library stays, with its name" yes "$(exists 'Film miei')"
check "and has lost the folders of the server" '[]' "$(locations 'Film miei')"
check "the stubs of the films are gone" 0 "$(strms "$FILM_ID")"
check "the page warns for both" '["Film","Serie"]' "$(vanished)"
check "nothing stays imported" '[]' "$(imported)"
echo "   libraries: $(get local /Library/VirtualFolders | jq -c '[.[].Name] | sort')"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
