#!/usr/bin/env bash
# Integration 8 of the 0.6 spec (F42): the shared libraries (§10 row 442, D29, D41-D44). Two stock remotes, one per type
# and one in "none" mode. The option goes on with no confirmation and makes "Tutti i server - Film" and "- Serie" with the
# type folders of both servers, blocked before the first scan for who blocks a server library on their folders. The
# same title from both servers is one item with two versions in each library that lists it (rows 63, 442), and the
# versions follow what each user sees. A type folder also stays in its server library, so a title shows in both with one
# watch state. A rename in the Dashboard is followed, a removed server leaves once its
# titles are gone, the settings save leaves the option alone, and the switch-off deletes the libraries after a confirmation.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-f42] [PORT=8290] shared.sh
# The containers are stopped at the end, not removed; the next run makes them again.
set -euo pipefail
PREFIX=${PREFIX:-jm-f42}
PORT=${PORT:-8290}
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"
trap 'docker stop "$PREFIX-local" "$PREFIX-remote" "$PREFIX-peer" >/dev/null 2>&1 || true' EXIT

# install: builds the plugin in the SDK container of the project and installs it, then restarts the server.
install() {
  local main src out version target
  main=$(git -C "$WT" worktree list | head -1 | awk '{print $1}')
  src=/src${WT#"$main"}
  out=/tmp/$PREFIX-build
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$WT/build.yaml")
  docker exec -w "$src" jellymesh-sdk sh -c "rm -rf $out && dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o $out --nologo -v quiet" >/dev/null
  jq -n --arg v "$version" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$WORK/meta.json"
  docker cp "jellymesh-sdk:$out/Jellyfin.Plugin.JellyMesh.dll" "$WORK/Jellyfin.Plugin.JellyMesh.dll"
  target=/config/plugins/JellyMesh_$version
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$WORK/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/"
  docker cp "$WORK/meta.json" "$PREFIX-local:$target/"
  docker restart "$PREFIX-local" >/dev/null
}

REMOTE_URL="http://host.docker.internal:$((PORT + 1))"
PEER_URL="http://host.docker.internal:$((PORT + 2))"
FILM="Tutti i server - Film"
SERIES="Tutti i server - Serie"
own() { get local /JellyMesh/Admin/Status | jq -c --arg id "$1" '.Servers[] | select(.Id == $id) | .AutomaticLibraries'; }
shared() { get local /JellyMesh/Admin/SharedLibraries; }
registry() { shared | jq -c '[.Libraries[] | [.Name, .Adopted]] | sort'; }
switch() { post local /JellyMesh/Admin/SharedLibraries "$1"; }
folder() { get local /Library/VirtualFolders | jq -c --arg n "$1" '.[] | select(.Name == $n)'; }
id_of() { folder "$1" | jq -r .ItemId; }
exists() { [ -n "$(folder "$1")" ] && echo yes || echo no; }
locations() { folder "$1" | jq -c '.Locations | sort'; }
wait_busy() { # wait_busy <command printing the state>: until its Busy is false
  local i
  for i in $(seq 1 300); do
    [ "$($1 | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the libraries job did not end" >&2
  return 1
}
own_casa() { own "$CASA"; }
# in_library <library id> <user id>: the federated films of a library the user sees, as "id played".
in_library() { get local "/Items?userId=$2&ParentId=$1&Recursive=true&IncludeItemTypes=Movie&Fields=Path" | jq -c '[.Items[] | "\(.Id) \(.UserData.Played)"] | sort'; }
# versions <library id> <title>: how many items of the title the library lists, and how many versions the first has.
# versions <library id> <title> [user id]: as the admin when no user is given.
versions() {
  get local "/Items?userId=${3:-$ME}&ParentId=$1&Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources" |
    jq -r --arg t "$2" '[.Items[] | select(.Name | contains($t))] | "\(length) \(.[0].MediaSources // [] | length)"'
}
kinds() { get local "/Items?userId=$1&Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path" | jq -c '[.Items[] | select((.Path // "") | startswith("/federata")) | .Type] | unique'; }
access() {
  get local /Users | jq -c --arg id "$1" --arg n "$2" 'def has: map(ascii_downcase | gsub("-"; "")) | index($id | ascii_downcase | gsub("-"; "")) != null;
    .[] | select(.Name == $n) | {Blocked: (.Policy.BlockedMediaFolders // [] | has)}'
}
dashed() { local id=$1; echo "${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"; }
# block <user id> <library id>: blocks one library with the server stopped; 12.4 does not save BlockedMediaFolders from
# the policy route.
block() {
  docker stop "$PREFIX-local" >/dev/null
  docker run --rm --name "$PREFIX-sqlite" -v "$PREFIX-local-config:/config" alpine sh -c \
    'apk add -q sqlite >/dev/null && sqlite3 /config/data/jellyfin.db "$1"' sh \
    "update Preferences set Value = '$(dashed "$2")' where Kind = 2 and UserId = '$(dashed "$1" | tr a-f A-F)';"
  docker start "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
}
# name_id <library name>: the id 12.4 gives a library of that name.
name_id() { python3 -I -c 'import hashlib, sys, uuid; key = "MediaBrowser.Controller.Entities.CollectionFolderroot\\default\\" + sys.argv[1]; print(uuid.UUID(bytes_le=hashlib.md5(key.encode("utf-16-le")).digest()).hex)' "$1"; }

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1)), $PREFIX-peer on $((PORT + 2))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
scan remote
start peer "$MEDIA/remoto-jellymesh:/media:ro"
library peer Film movies /media/film
library peer Serie tvshows /media/serie
user peer jellymesh >/dev/null
scan peer

start local "$PREFIX-media:/media"
install
sleep 3
wait_ready local
login local
ME=$(get local /Users/Me | jq -r .Id)
settings local '.FederatedFolderPath = "/federata"' >/dev/null
CASA=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "$REMOTE_URL" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"ByType"}')" | jq -r .Id)
MARE=$(post local /JellyMesh/Admin/Servers "$(jq -n --arg u "$PEER_URL" '{Name:"Mare",Url:$u,Username:"jellymesh",Password:"jellymesh-test",LibraryMode:"None"}')" | jq -r .Id)
import_all local "$CASA"
import_all local "$MARE"
sync_now local
wait_busy own_casa
scan local
check "the server per type has its libraries, the one in none has none" '["Casa - Film","Casa - Serie"]' "$(get local /Library/VirtualFolders | jq -c '[.[].Name] | sort')"
CASA_FILM=$(id_of 'Casa - Film')
CASA_FILM_PATHS=$(locations 'Casa - Film')
CASA_SERIES_PATHS=$(locations 'Casa - Serie')
BLOCKING=$(user local bloccato)
block "$BLOCKING" "$CASA_FILM"
check "the blocking user sees only episodes" '["Episode"]' "$(kinds "$BLOCKING")"
check "the option is off" false "$(shared | jq -r .Enabled)"

echo "--- switch-on: no confirmation, one library per type with the folders of both servers [row 442, D29, D42]"
switch '{"Enabled":true}' >/dev/null
check "the switch-on answers 202" 202 "$(status)"
wait_busy shared
scan local
check "both shared libraries are made" "[[\"$FILM\",false],[\"$SERIES\",false]]" "$(registry)"
check "no error and no conflict" "None []" "$(shared | jq -rc '"\(.Error) \(.Conflicts)"')"
MARE_FILM_PATHS=$(own "$MARE" | jq -c '[.ManualPaths[] | select(.LibraryType == "movies") | .Path]')
MARE_SERIES_PATHS=$(own "$MARE" | jq -c '[.ManualPaths[] | select(.LibraryType == "tvshows") | .Path]')
check "the film library has the film folders of both servers" "$(jq -nc --argjson a "$CASA_FILM_PATHS" --argjson b "$MARE_FILM_PATHS" '$a + $b | sort')" "$(locations "$FILM")"
check "the series library has the series folders of both servers" "$(jq -nc --argjson a "$CASA_SERIES_PATHS" --argjson b "$MARE_SERIES_PATHS" '$a + $b | sort')" "$(locations "$SERIES")"
check "each server has folders in it" "true true" "$(locations "$FILM" | jq -r --arg c "$(server_folder "$CASA")" --arg m "$(server_folder "$MARE")" '"\(any(.[]; startswith($c))) \(any(.[]; startswith($m)))"')"
check "the server library keeps its type folders [D29]" "$CASA_FILM_PATHS" "$(locations 'Casa - Film')"
SHARED_FILM=$(id_of "$FILM")
check "the shared film library has the id of its name" "$(name_id "$FILM")" "$SHARED_FILM"
check "who blocks the server film library blocks the shared one, from before its first scan [D42]" '{"Blocked":true}' "$(access "$SHARED_FILM" bloccato)"
check "and not the shared series library" '{"Blocked":false}' "$(access "$(id_of "$SERIES")" bloccato)"
check "the blocking user still sees only episodes" '["Episode"]' "$(kinds "$BLOCKING")"

echo "--- versions of both servers are one item in each library that lists the title, as each user sees them [rows 63, 442]"
check "the shared library lists the title once with the versions of both servers" "1 2" "$(versions "$SHARED_FILM" "Confronto Risoluzione")"
check "the server library lists it once with the versions the admin sees, the group being one for the server" "1 2" "$(versions "$CASA_FILM" "Confronto Risoluzione")"
NO_SHARED=$(user local senza-tutti)
block "$NO_SHARED" "$SHARED_FILM"
check "who blocks the shared film library gets only the version of the server library" "1 1" "$(versions "$CASA_FILM" "Confronto Risoluzione" "$NO_SHARED")"

echo "--- a title of a type folder shows in its server library and the shared one with one watch state [D29]"
TEARS=$(stub_item local "Tears of Steel")
post local "/UserItems/$TEARS/UserData?userId=$ME" '{"Played":true}' >/dev/null
check "the film is played in the server library" "true" "$(in_library "$CASA_FILM" "$ME" | jq -r --arg id "$TEARS" '.[] | select(startswith($id)) | split(" ")[1]')"
check "and the same item is played in the shared library" "true" "$(in_library "$SHARED_FILM" "$ME" | jq -r --arg id "$TEARS" '.[] | select(startswith($id)) | split(" ")[1]')"

echo "--- a rename in the Dashboard is followed, not undone, and no library is made again [row 442]"
req local POST "/Library/VirtualFolders/Name?name=$(jq -rn --arg v "$FILM" '$v | @uri')&newName=Film%20di%20tutti&refreshLibrary=true" >/dev/null
check "the rename answers 204" 204 "$(status)"
sync_now local
wait_busy shared
check "the registry follows the new name" "[[\"Film di tutti\",false],[\"$SERIES\",false]]" "$(registry)"
check "no library takes the old name again" no "$(exists "$FILM")"
check "the renamed library keeps the folders" "$(jq -nc --argjson a "$CASA_FILM_PATHS" --argjson b "$MARE_FILM_PATHS" '$a + $b | sort')" "$(locations 'Film di tutti')"
SHARED_FILM=$(id_of 'Film di tutti')

echo "--- the settings save leaves the option alone"
check "the settings save answers 200" 200 "$(settings local '.')"
check "the option is still on" true "$(shared | jq -r .Enabled)"

echo "--- a removed server leaves the shared libraries once its titles are gone [D44]"
req local DELETE "/JellyMesh/Admin/Servers/$MARE" >/dev/null
check "the removal answers 204" 204 "$(status)"
for i in $(seq 1 120); do
  [ "$(locations 'Film di tutti')" = "$CASA_FILM_PATHS" ] && break
  sleep 1
done
check "the film library keeps only the folders of the server left" "$CASA_FILM_PATHS" "$(locations 'Film di tutti')"
check "the series library too" "$CASA_SERIES_PATHS" "$(locations "$SERIES")"
check "no title of the removed server is left on disk" "" "$(docker exec "$PREFIX-local" sh -c "find $(server_folder "$MARE") -name '*.strm' 2>/dev/null | head -1")"

echo "--- switch-off: a confirmation names the libraries, then they are deleted and the server libraries stay [row 442]"
answer=$(switch '{"Enabled":false}')
check "the switch-off asks to confirm" 409 "$(status)"
check "it names the libraries it deletes, and none to release" "[\"Film di tutti\",\"$SERIES\"] []" "$(echo "$answer" | jq -c '.ToDelete | sort' | tr -d '\n'; echo -n ' '; echo "$answer" | jq -c .ToRelease)"
check "nothing is deleted yet" "yes true" "$(exists 'Film di tutti') $(shared | jq -r .Enabled)"
switch '{"Enabled":false,"Confirmed":true}' >/dev/null
check "the confirmed switch-off answers 202" 202 "$(status)"
wait_busy shared
check "the shared libraries are deleted, the server libraries stay" '["Casa - Film","Casa - Serie"]' "$(get local /Library/VirtualFolders | jq -c '[.[].Name] | sort')"
check "the option is off with an empty registry" "false []" "$(shared | jq -rc '"\(.Enabled) \(.Libraries)"')"
check "the film keeps its id and its watch state" "true" "$(in_library "$CASA_FILM" "$ME" | jq -r --arg id "$TEARS" '.[] | select(startswith($id)) | split(" ")[1]')"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
