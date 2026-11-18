#!/usr/bin/env bash
# Integration 6 of the 0.6 spec (F40): a change of library mode from the Edit form. The libraries of the new mode are
# made and scanned before the plugin's old ones go, item ids and watch state stay (F23), "none" deletes only the plugin's
# libraries and lists the manual paths, and the mode changes with the remote down (§10 row 438, D28). A user with access
# to the film library only and a user who blocks the series library see the same titles after each change. When two film
# libraries merge into one, only a user who saw both gets it; who saw one loses it and is named, before and after.
# A stock remote with two film libraries and a series library, and a consumer with the plugin.
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
episode() { get local '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path' | jq -r --arg t "$1" '[.Items[] | select((.Path // "") | (contains($t) and startswith("/federata")))][0].Id // ""'; }
played() { get local "/UserItems/$1/UserData?userId=$ME" | jq -r .Played; }
# watched: the state of the film and the two episodes marked before the first change.
watched() { echo "$(played "$TEARS") $(played "$E3") $(get local "/UserItems/$E4/UserData?userId=$ME" | jq -r .PlaybackPositionTicks)"; }
# ids: the ids of the federated films and episodes, sorted, to compare before and after a change.
# sees <user id>: the ids of the federated films and episodes the user sees, sorted.
sees() { get local "/Items?userId=$1&Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path" | jq -c '[.Items[] | select((.Path // "") | startswith("/federata")) | .Id] | sort'; }
kinds() { get local "/Items?userId=$1&Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path" | jq -c '[.Items[] | select((.Path // "") | startswith("/federata")) | .Type] | unique'; }
# policy <user id> <jq filter>: changes the policy of a local user.
policy() { local p; p=$(get local "/Users/$1" | jq -c ".Policy | $2"); post local "/Users/$1/Policy" "$p" >/dev/null; }
# seen: what both users see, to compare before and after a change.
seen() { echo "$(sees "$LIMITED") $(sees "$BLOCKING")"; }
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
ids() { get local '/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path' | jq -c '[.Items[] | select((.Path // "") | startswith("/federata")) | "\(.Id) \(.Path | split("/") | last)"] | sort'; }

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
START_ENV="-v $MEDIA/remoto-jellymesh/film:/bambini:ro" start remote "$MEDIA/remoto-standard:/media:ro"
library remote "Film HD" movies /media/film
library remote "Film Bambini" movies /bambini
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
E3=$(episode "S01E03")
E4=$(episode "S01E04")
post local "/UserItems/$E3/UserData?userId=$ME" '{"Played":true}' >/dev/null
post local "/UserItems/$E4/UserData?userId=$ME" '{"PlaybackPositionTicks":600000000}' >/dev/null
check "the film and an episode are played, another episode is half seen" "true true 600000000" "$(watched)"
BEFORE=$(ids)
LIMITED=$(user local ristretto)
BLOCKING=$(user local bloccato)
policy "$LIMITED" ".EnableAllFolders = false | .EnabledFolders = [\"$(folder 'Casa - Film' | jq -r .ItemId)\"]"
block "$BLOCKING" "$(folder 'Casa - Serie' | jq -r .ItemId)"
check "the restricted user sees only films" '["Movie"]' "$(kinds "$LIMITED")"
check "the user blocking the series library sees only films" '["Movie"]' "$(kinds "$BLOCKING")"
SEEN=$(seen)

echo "--- the check lists the change and saves nothing [row 438]"
answer=$(check_edit '{"LibraryMode":"ByRemoteLibrary"}')
check "the check answers 200" 200 "$(status)"
check "it lists the libraries the change deletes" '["Casa - Film","Casa - Serie"]' "$(echo "$answer" | jq -c '.Consequences.ModeChange.Deleted | sort')"
check "the mode is still per type" ByType "$(own | jq -r .Mode)"

echo "--- per type to per remote library: new libraries made and scanned, then the old ones deleted [row 438, F23]"
save_edit '{"LibraryMode":"ByRemoteLibrary"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the libraries of the remote libraries replace those per type" '[["Casa - Film Bambini",false],["Casa - Film HD",false],["Casa - Telefilm",false]]' "$(registry)"
check "the old libraries are gone" "no no" "$(exists 'Casa - Film') $(exists 'Casa - Serie')"
check "the film libraries have the film folders" "$(own | jq -c '[.ManualPaths[] | select(.LibraryType == "movies") | .Path] | sort')" \
  "$(jq -nc --argjson a "$(locations 'Casa - Film HD')" --argjson b "$(locations 'Casa - Film Bambini')" '$a + $b | sort')"
check "the items keep their ids" "$BEFORE" "$(ids)"
check "the film keeps its id" "$TEARS" "$(film "Tears of Steel")"
check "the watch state stays" "true true 600000000" "$(watched)"
check "the restricted and the blocking user see what they saw" "$SEEN" "$(seen)"

echo "--- two film libraries merge: only a user who saw both gets the merged one, the others are named [row 438]"
CHILDREN=$(user local bambini)
BOTH=$(user local entrambi)
NO_HD=$(user local noadulti)
policy "$CHILDREN" ".EnableAllFolders = false | .EnabledFolders = [\"$(folder 'Casa - Film Bambini' | jq -r .ItemId)\"]"
policy "$BOTH" ".EnableAllFolders = false | .EnabledFolders = [\"$(folder 'Casa - Film Bambini' | jq -r .ItemId)\", \"$(folder 'Casa - Film HD' | jq -r .ItemId)\"]"
block "$NO_HD" "$(folder 'Casa - Film HD' | jq -r .ItemId)"
check "the user with the children films sees films" '["Movie"]' "$(kinds "$CHILDREN")"
check "the user with both film libraries sees films" '["Movie"]' "$(kinds "$BOTH")"
check "the user blocking the HD films sees children films and episodes" '["Episode","Movie"]' "$(kinds "$NO_HD")"
BOTH_SAW=$(sees "$BOTH")
LOST='[{"Library":"Casa - Film","Users":["bambini","noadulti"]}]'
answer=$(check_edit '{"LibraryMode":"ByType"}')
check "the check names who loses titles in the merged library" "$LOST" "$(echo "$answer" | jq -c '[.Consequences.ModeChange.LostAccess[] | .Users |= sort]')"

echo "--- with the remote down the mode still changes [D28]"
docker stop "$PREFIX-remote" >/dev/null
save_edit '{"LibraryMode":"ByType"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the per type libraries are back" '[["Casa - Film",false],["Casa - Serie",false]]' "$(registry)"
check "the items keep their ids" "$BEFORE" "$(ids)"
check "the watch state stays" "true true 600000000" "$(watched)"
check "the restricted and the blocking user see what they saw" "$SEEN" "$(seen)"
check "the state names who lost titles" "$LOST" "$(own | jq -c '[.LostAccess[] | .Users |= sort]')"
check "the user with only the children films sees no film" '[]' "$(sees "$CHILDREN")"
check "the user with both film libraries keeps the films" "$BOTH_SAW" "$(sees "$BOTH")"
check "the user blocking the HD films sees only episodes" '["Episode"]' "$(kinds "$NO_HD")"
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "the state still names them after a restart" "$LOST" "$(own | jq -c '[.LostAccess[] | .Users |= sort]')"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote

echo "--- to none: only the plugin's libraries go, an admin library stays, the paths are listed [row 438]"
library local "Mie" movies /media
save_edit '{"LibraryMode":"None"}' >/dev/null
check "the save answers 200" 200 "$(status)"
wait_own
check "the registry is empty" '[]' "$(registry)"
check "the plugin's libraries are deleted, the admin's stays" '["Mie"]' "$(lib_names)"
check "the state lists the two film paths and a series path" '["movies","movies","tvshows"]' "$(own | jq -c '[.ManualPaths[].LibraryType] | sort')"
check "the mode is none" None "$(own | jq -r .Mode)"
check "with the plugin's libraries gone neither user sees a federated title" "[] []" "$(seen)"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
