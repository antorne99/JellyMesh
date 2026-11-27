#!/usr/bin/env bash
# Integration 7 of the 0.6 spec (F41): an admin library with exactly the paths and type the plugin would give is adopted,
# one partly on them is neither made nor adopted and the state says why, and an adopted library is released, never deleted
# (§10 rows 440-441, D4, D30). A stock remote with a film and a series library, and a consumer with the plugin.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jellymesh-probe-f41] [PORT=18990] [KEEP=1] adoption.sh
set -euo pipefail
PREFIX=${PREFIX:-jellymesh-probe-f41}
PORT=${PORT:-18990}
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
registry() { own | jq -c '[.Libraries[] | [.Name, .Adopted]]'; }
conflicts() { own | jq -c '[.Conflicts[] | [.Library, (.Reason | tostring)]]'; }
folder() { get local /Library/VirtualFolders | jq -c --arg n "$1" '.[] | select(.Name == $n)'; }
exists() { [ -n "$(folder "$1")" ] && echo yes || echo no; }
locations() { folder "$1" | jq -c '.Locations | sort'; }
type_of() { folder "$1" | jq -r '.CollectionType // ""'; }
lib_names() { get local /Library/VirtualFolders | jq -c '[.[].Name] | sort'; }
# form <json>: the edit form of the server with those fields changed.
form() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" --argjson c "$1" '.Servers[] | select(.Id == $id) | {Name, Url, Username: "", Password: ""} + $c'; }
switch() { # switch <mode>: saves the library mode with the edit form, and prints the status
  req local PUT "/JellyMesh/Admin/Servers/$SERVER" "$(form "{\"LibraryMode\":\"$1\"}")" >/dev/null
  status
}
consequences() { post local "/JellyMesh/Admin/Servers/$SERVER/Edit/Check" "$(form "$1")" | jq -c .Consequences; }
wait_own() {
  local i
  for i in $(seq 1 300); do
    [ "$(own | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the libraries job did not end" >&2
  return 1
}
remove_library() { req local DELETE "/Library/VirtualFolders?name=$(jq -rn --arg v "$1" '$v | @uri')&refreshLibrary=false" >/dev/null; }
# add_paths <library> <json array of paths>: adds each path, as an admin does in the dashboard.
add_paths() {
  local p
  for p in $(echo "$2" | jq -r '.[]'); do link_folder local "$1" "$p" >/dev/null; done
}
# make <name> <type> <json array of paths>: an admin library with those paths.
make() {
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

start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/extra && chmod -R a+rwX /media'
install
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
SERVER=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$((PORT + 1))" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"None"}')" | jq -r .Id)
FOLDER=$(server_folder "$SERVER")
import_all local "$SERVER"
sync_now local
scan local
FILM_PATHS=$(own | jq -c '[.ManualPaths[] | select(.LibraryType == "movies") | .Path] | sort')
SERIE_PATHS=$(own | jq -c '[.ManualPaths[] | select(.LibraryType == "tvshows") | .Path] | sort')
echo "   server $SERVER, films $FILM_PATHS, series $SERIE_PATHS"
check "in none mode the state lists a film and a series folder" "1 1" "$(echo "$FILM_PATHS" | jq length) $(echo "$SERIE_PATHS" | jq length)"

echo "--- an exact library is adopted, one with an extra path blocks the series library [rows 440-441, D30]"
make "Film miei" movies "$FILM_PATHS"
make "Serie mie" tvshows "$(echo "$SERIE_PATHS" | jq -c '. + ["/media/extra"]')"
check "switching on answers 200" 200 "$(switch ByType)"
wait_own
scan local
check "the exact library is adopted, the series one is not made" '[["Film miei",true]]' "$(registry)"
check "no Casa - Film is made next to it" no "$(exists 'Casa - Film')"
check "no Casa - Serie is made next to the library with an extra path" no "$(exists 'Casa - Serie')"
check "the state says why" '[["Serie mie","OtherPaths"]]' "$(conflicts)"
check "the adopted library keeps its paths" "$FILM_PATHS" "$(locations 'Film miei')"
check "the library with an extra path is untouched" "$(echo "$SERIE_PATHS" | jq -c '. + ["/media/extra"] | sort')" "$(locations 'Serie mie')"
check "the adopted library shows the titles of the server" 1 "$([ "$(get local "/Items?ParentId=$(folder 'Film miei' | jq -r .ItemId)&Recursive=true&IncludeItemTypes=Movie" | jq .TotalRecordCount)" -gt 0 ] && echo 1 || echo 0)"

echo "--- a library on a parent folder blocks it [D4]"
remove_library "Serie mie"
make Tutto tvshows "[\"$FOLDER\"]"
sync_now local
check "no Casa - Serie under a library on the server folder" no "$(exists 'Casa - Serie')"
check "the state says it is on a parent folder" '[["Tutto","ParentPath"]]' "$(conflicts)"

echo "--- a library of another type on the paths blocks it [D4]"
remove_library Tutto
make "Serie come film" movies "$SERIE_PATHS"
sync_now local
check "no Casa - Serie under a library of another type" no "$(exists 'Casa - Serie')"
check "the state says it has another type" '[["Serie come film","OtherType"]]' "$(conflicts)"

echo "--- once the admin clears the way, the next check makes the library"
remove_library "Serie come film"
sync_now local
check "Casa - Serie is made" "$SERIE_PATHS" "$(locations 'Casa - Serie')"
check "nothing is in the way any more" '[]' "$(conflicts)"
check "the registry holds both" '[["Casa - Serie",false],["Film miei",true]]' "$(registry | jq -c 'sort')"

echo "--- a server rename never renames the adopted library"
check "the rename lists only the library the plugin made" '["Casa - Serie"]' "$(consequences '{"Name":"Mare"}' | jq -c .Rename.Libraries)"

echo "--- switching off releases the adopted library and deletes only the one the plugin made [row 440]"
changes=$(consequences '{"LibraryMode":"None"}')
check "the confirmation names only the library the plugin made" '["Casa - Serie"]' "$(echo "$changes" | jq -c .ModeChange.Deleted)"
check "and the adopted one it releases" '["Film miei"]' "$(echo "$changes" | jq -c .ModeChange.Released)"
check "confirmed switch off answers 200" 200 "$(switch None)"
wait_own
check "Casa - Serie is deleted" no "$(exists 'Casa - Serie')"
check "the adopted library stays, with its type" "yes movies" "$(exists 'Film miei') $(type_of 'Film miei')"
check "and has lost only the folders of the server" '[]' "$(locations 'Film miei')"
check "the registry is empty" '[]' "$(registry)"

echo "--- removing the server releases an adopted library once its titles are gone [140, 278]"
add_paths "Film miei" "$FILM_PATHS"
check "switching on again answers 200" 200 "$(switch ByType)"
wait_own
check "the exact library is adopted again" '[["Film miei",true]]' "$(registry | jq -c 'map(select(.[0] == "Film miei"))')"
req local DELETE "/JellyMesh/Admin/Servers/$SERVER" >/dev/null
check "the removal answers 204" 204 "$(status)"
for i in $(seq 1 300); do
  [ "$(exists 'Casa - Serie')" = no ] && [ "$(locations 'Film miei')" = '[]' ] && break
  sleep 1
done
check "the library the plugin made is deleted" no "$(exists 'Casa - Serie')"
check "the adopted library stays" yes "$(exists 'Film miei')"
check "without the folders of the server" '[]' "$(locations 'Film miei')"
echo "   libraries: $(lib_names)"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
