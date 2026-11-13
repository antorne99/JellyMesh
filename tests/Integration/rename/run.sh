#!/usr/bin/env bash
# Integration 9 of the per-server spec: a server renamed end to end (Q38, §10 rows 84-86), leaving no old library behind (row 318).
# A stock remote and a consumer with automatic libraries, a local copy linked to a stub, a limited and a blocked user.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-rename] [PORT=18960] [KEEP=1] run.sh
set -euo pipefail
PREFIX=${PREFIX:-jm-rename}
PORT=${PORT:-18960}
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"
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

# as <user> <method> <path>: as a user of the consumer; prints the body, the status goes to $WORK/status.
as() {
  local who=$1 method=$2 path=$3 auth
  auth="MediaBrowser Client=\"JellyMeshRename\", Device=\"$who\", DeviceId=\"$PREFIX-$who\", Version=\"0.1\""
  [ -s "$WORK/tok-$who" ] || curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $auth" \
    -d "{\"Username\":\"$who\",\"Pw\":\"jellymesh-test\"}" "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/tok-$who"
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" \
    -H "Authorization: $auth, Token=\"$(cat "$WORK/tok-$who")\"" "http://127.0.0.1:$PORT$path" >"$WORK/status"
  cat "$WORK/body"
}
only() { # only <user> <library>...: the user sees those libraries alone
  local user=$1 libs uid policy
  shift
  libs=$(get local /Library/VirtualFolders | jq -c --args '[.[] | select(.Name | IN($ARGS.positional[])) | .ItemId]' "$@")
  uid=$(user_id local "$user")
  policy=$(get local "/Users/$uid" | jq -c --argjson f "$libs" '.Policy | .EnableAllFolders = false | .EnabledFolders = $f')
  post local "/Users/$uid/Policy" "$policy" >/dev/null
}
# stopped <sql>: runs a statement on the database of the consumer while it is stopped, then starts it again.
stopped() {
  docker stop "$PREFIX-local" >/dev/null
  docker run --rm --name "$PREFIX-sqlite" -v "$PREFIX-local-config:/config" alpine \
    sh -c 'apk add -q sqlite >/dev/null && sqlite3 /config/data/jellyfin.db "$1"' sh "$1"
  docker start "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
}
# block_sql <user id> <library id>: blocks one library; 12.4 does not save BlockedMediaFolders from the policy route.
block_sql() {
  local uid
  uid=$(dashed "$1" | tr a-f A-F)
  echo "update Preferences set Value = '$(dashed "$2")' where Kind = 2 and UserId = '$uid';"
}
blocks() { get local "/Users/$ALL" | jq -c '[.Policy.BlockedMediaFolders[] | ascii_downcase | gsub("-"; "")]'; }
# probe <user> <id>: what the item routes answer to an id, with the records of the list routes [row 291].
probe() {
  local uid id a b c d e
  uid=$(user_id local "$1" | tr -d -)
  id=$(dashed "$2")
  as "$1" GET "/Items/$id" >/dev/null; a=$(status)
  as "$1" GET "/Users/$uid/Items/$id" >/dev/null; b=$(status)
  as "$1" GET "/Items/$id/Images" >/dev/null; c=$(status)
  d=$(as "$1" GET "/Items?ids=$id" | jq -r '.TotalRecordCount // "-"' 2>/dev/null || echo -); d="$(status)/$d"
  e=$(as "$1" GET "/Items?ParentId=$id&Recursive=true" | jq -r '.TotalRecordCount // "-"' 2>/dev/null || echo -); e="$(status)/$e"
  echo "$a $b $c $d $e"
}
lib_id() { get local /Library/VirtualFolders | jq -r --arg n "$1" '.[] | select(.Name == $n) | .ItemId // ""' | tr -d -; }
lib_names() { get local /Library/VirtualFolders | jq -c '[.[].Name] | sort'; }
# db_libs: the CollectionFolder rows of Jellyfin's database, which also keep the libraries a rename left behind [row 318].
db_libs() { sql "select Name from BaseItems where Type like '%.CollectionFolder' order by Name;" | jq -Rnc '[inputs] | sort'; }
views() { as all GET "/UserViews?userId=$ALL" | jq -c '[.Items[].Name] | sort'; }
# ghosts <moment>: no old library is left in the database, in the views of a user who sees all, or by id [row 318].
ghosts() {
  local id
  check "$1: Jellyfin keeps no row of an old library [row 318]" "$(lib_names)" "$(db_libs)"
  check "$1: the views of a user who sees all but Film have no old library" "$(lib_names | jq -c 'map(select(endswith(" - Film") | not))')" "$(views)"
  for id in "${OLD_SERIES[@]}"; do
    as lim GET "/Items?ids=$(dashed "$id")" >/dev/null
    check "$1: the limited user gets no record by the old Serie id $id" 0 "$(jq -r '.TotalRecordCount // 0' "$WORK/body")"
    as all GET "/Items/$(dashed "$id")" >/dev/null
    check "$1: nor does a user who sees all open it" 404 "$(status)"
  done
}
OLD_SERIES=()
changes() { get local /JellyMesh/Admin/Status | jq -c .FederatedFolder.Changes; }
rename_state() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .Rename'; }
wait_rename() {
  local i
  for i in $(seq 1 600); do
    [ "$(rename_state | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the rename did not end" >&2
  return 1
}
wait_own() {
  local i
  for i in $(seq 1 300); do
    [ "$(get local /JellyMesh/Admin/Status | jq -r --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .AutomaticLibraries.Busy')" = false ] && return 0
    sleep 1
  done
  return 1
}
strms() { docker exec "$PREFIX-local" sh -c "find '$FOLDER' -name '*$1.strm' | wc -l" | tr -d ' '; }
played() { get local "/Users/$2/Items/$(dashed "$1")" | jq -r .UserData.Played; }
position() { get local "/Users/$2/Items/$(dashed "$1")" | jq -r .UserData.PlaybackPositionTicks; }
film() { vid "$FOLDER/Film/$1%- $2.strm"; }
episode() { vid "$FOLDER/Serie/JM Serie Divisa%S01E03%- $1.strm"; }
bare_episode() { vid "$FOLDER/Serie/Serie Senza ID%S01E02%- $1.strm"; }
home() { get local "/Users/$LIM" | jq -c --arg id "$1" '.Configuration | [(.OrderedViews | index($id) != null), (.MyMediaExcludes | index($id) != null), (.LatestItemsExcludes | index($id) != null)]'; }
count_as() { as "$1" GET "/Items?ParentId=$(dashed "$2")&Recursive=true&IncludeItemTypes=$3" | jq -r '.TotalRecordCount // 0'; }

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1))"
down
mkdir -p "$WORK"
rm -f "$WORK"/tok-*
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-media:/media"
SONDA="Sonda Locale Senza ID (2026)"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film /media/serie && touch /media/serie/.keep'
docker cp "$MEDIA/locale/film/$SONDA" "$PREFIX-local:/media/film/"
docker exec "$PREFIX-local" sh -c 'chmod -R a+rwX /media'
library local Film movies /media/film
install
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
SERVER=$(add_remote local Casa "http://host.docker.internal:$((PORT + 1))" jellymesh)
FOLDER=$(server_folder "$SERVER")
import_all local "$SERVER"
scan local
sync_now local
post local "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":true}' >/dev/null
check "automatic libraries switched on" 202 "$(status)"
wait_own
scan local
sync_now local
check "the libraries of Casa exist" '["Casa - Film","Casa - Serie","Film"]' "$(lib_names)"
for u in lim blk all; do user local "$u" >/dev/null; done
only lim "Casa - Film" "Casa - Serie" Film
only blk "Casa - Serie" Film
ALL=$(user_id local all | tr -d -)
policy=$(get local "/Users/$ALL" | jq -c '.Policy | .EnableAllFolders = true')
post local "/Users/$ALL/Policy" "$policy" >/dev/null
stopped "$(block_sql "$ALL" "$(lib_id "Casa - Film")")"
ADMIN=$(user_id local admin | tr -d -)
LIM=$(user_id local lim | tr -d -)

OLD_FILM_LIB=$(lib_id "Casa - Film")
OLD_SERIE_LIB=$(lib_id "Casa - Serie")
LOCAL_SONDA=$(vid "/media/film/$SONDA/")
TEARS=$(film "Tears of Steel" Casa)
SONDA_STUB=$(film "Sonda Locale" Casa)
EP=$(episode Casa)
CASA_STRMS=$(strms Casa)
echo "   server $SERVER, $CASA_STRMS stubs, tears $TEARS, episode $EP"
check "the stub of the local copy is linked to it" "$LOCAL_SONDA" "$(primary_of "$SONDA_STUB")"
post local "/UserPlayedItems/$(dashed "$TEARS")?userId=$ADMIN" >/dev/null
post local "/UserPlayedItems/$(dashed "$TEARS")?userId=$LIM" >/dev/null
post local "/UserPlayedItems/$(dashed "$EP")?userId=$LIM" >/dev/null
post local "/UserItems/$(dashed "$EP")/UserData?userId=$ADMIN" '{"PlaybackPositionTicks": 300000000}' >/dev/null
# Titles with no provider id: only the carrier can bring their state to the new items.
BARE=$(film "Sonda Senza ID" Casa)
BARE_EP=$(bare_episode Casa)
post local "/UserPlayedItems/$(dashed "$BARE")?userId=$LIM" >/dev/null
post local "/UserPlayedItems/$(dashed "$BARE_EP")?userId=$LIM" >/dev/null
# The home lists of the limited user name the Film library by its id.
config=$(get local "/Users/$LIM" | jq -c --arg id "$(dashed "$OLD_FILM_LIB" | tr -d -)" '.Configuration | .OrderedViews = [$id] | .MyMediaExcludes = [$id] | .LatestItemsExcludes = [$id]')
post local "/Users/Configuration?userId=$LIM" "$config" >/dev/null
check "the home lists are saved" '[true,true,true]' "$(home "$OLD_FILM_LIB")"
check "the user who blocks Film blocks it" "[\"$OLD_FILM_LIB\"]" "$(blocks)"
as all GET "/Items/$(dashed "$TEARS")" >/dev/null
check "and cannot open its film" 404 "$(status)"
check "the limited user sees the Film library" 1 "$([ "$(count_as lim "$OLD_FILM_LIB" Movie)" -gt 0 ] && echo 1 || echo 0)"
check "no library change since the sync" '{"Added":[],"Removed":[]}' "$(changes)"

rename_to() { # rename_to <old> <new>: renames the server and checks what Q38 asks
  local old=$1 new=$2 answer
  echo "--- rename $old to $new"
  local old_film=$(lib_id "$old - Film") old_serie=$(lib_id "$old - Serie")
  answer=$(post local "/JellyMesh/Admin/Servers/$SERVER/Rename" "{\"Name\":\"$new\"}")
  check "without a confirmation: 409" 409 "$(status)"
  check "listing the libraries it renames" "[\"$old - Film\",\"$old - Serie\"]" "$(echo "$answer" | jq -c .Libraries)"
  post local "/JellyMesh/Admin/Servers/$SERVER/Rename" "{\"Name\":\"$new\",\"Confirmed\":true}" >/dev/null
  check "confirmed: 202" 202 "$(status)"
  check "the rename running is no library change" '{"Added":[],"Removed":[]}' "$(changes)"
  wait_rename
  OLD_SERIES+=("$old_serie")
  check "the rename is done" '{"Busy":false,"Pending":false,"Failed":false}' "$(rename_state)"
  ghosts "right after the rename"
  check "the renamed libraries are no library change" '{"Added":[],"Removed":[]}' "$(changes)"
  check "the libraries follow the server" "$(jq -nc --arg n "$new" '["Film", "\($n) - Film", "\($n) - Serie"] | sort')" "$(lib_names)"
  check "no stub has the old label" 0 "$(strms "$old")"
  check "every stub has the new label" "$CASA_STRMS" "$(strms "$new")"
  local tears ep sonda film_lib serie_lib
  tears=$(film "Tears of Steel" "$new")
  ep=$(episode "$new")
  sonda=$(film "Sonda Locale" "$new")
  film_lib=$(lib_id "$new - Film")
  serie_lib=$(lib_id "$new - Serie")
  check "the old film item is gone" "" "$(vid "$FOLDER/Film/Tears of Steel%- $old.strm")"
  check "the film is played for the admin" true "$(played "$tears" "$ADMIN")"
  check "and for the limited user" true "$(played "$tears" "$LIM")"
  check "the episode is played for the limited user" true "$(played "$ep" "$LIM")"
  check "the admin keeps the position of the episode" 300000000 "$(position "$ep" "$ADMIN")"
  check "a film with no provider id is played for the limited user" true "$(played "$(film "Sonda Senza ID" "$new")" "$LIM")"
  check "an episode with no provider id is played for the limited user" true "$(played "$(bare_episode "$new")" "$LIM")"
  check "the home lists name the new Film id" '[true,true,true]' "$(home "$film_lib")"
  check "and no longer the old one" '[false,false,false]' "$(home "$old_film")"
  check "the stub of the local copy is linked again" "$LOCAL_SONDA" "$(primary_of "$sonda")"
  check "the limited user sees the renamed Film library" 1 "$([ "$(count_as lim "$film_lib" Movie)" -gt 0 ] && echo 1 || echo 0)"
  check "and the renamed Serie library" 1 "$([ "$(count_as lim "$serie_lib" Episode)" -gt 0 ] && echo 1 || echo 0)"
  check "the blocked user still sees the Serie library" 1 "$([ "$(count_as blk "$serie_lib" Episode)" -gt 0 ] && echo 1 || echo 0)"
  as blk GET "/Items/$(dashed "$tears")" >/dev/null
  check "the blocked user still cannot open the film" 404 "$(status)"
  as blk GET "/Items?ParentId=$(dashed "$old_film")&Recursive=true" >/dev/null
  check "the blocked user gets 404 on the old Film id [row 86]" 404 "$(status)"
  as lim GET "/Items?ParentId=$(dashed "$old_serie")&Recursive=true" >/dev/null
  check "the limited user gets 404 on the old Serie id [row 86]" 404 "$(status)"
  check "the user who blocks Film blocks the new id only [row 290]" "[\"$film_lib\"]" "$(blocks)"
  as all GET "/Items/$(dashed "$tears")" >/dev/null
  check "and still cannot open the film" 404 "$(status)"
  check "nor the old Film id by any item route [row 291]" "404 404 404 200/0 404/-" "$(probe all "$old_film")"
  check "nor can the limited user" "404 404 404 200/0 404/-" "$(probe lim "$old_film")"
  check "the page asks for a restart" true "$(get local /JellyMesh/Admin/Status | jq -r .RenamedLibrariesNeedRestart)"
  sync_now local
  post local "/Items/$(dashed "$tears")/PlaybackInfo?userId=$ADMIN" "$DIRECT" >/dev/null
  check "the renamed film plays after a sync" 200 "$(status)"
  check "its .strm holds no token" "" "$(docker exec "$PREFIX-local" sh -c "grep -il 'token\|api_key' \"\$(find '$FOLDER/Film' -name '*Tears of Steel*- $new.strm')\"" || true)"
  scan local
  ghosts "after a scan"
}

rename_to Casa Mare
rename_to Mare Alba

echo "--- a library rename left with a user to rewrite [row 290], and a record bound to no library [row 329]"
# The old id of a rename is that of its old folder: a library made, read and deleted gives a real one.
library local "Fantasma - Film" movies
for i in $(seq 1 30); do GHOST=$(lib_id "Fantasma - Film"); [ -n "$GHOST" ] && break; sleep 1; done
req local DELETE "/Library/VirtualFolders?name=Fantasma%20-%20Film&refreshLibrary=true" >/dev/null
check "the library of the old name is gone, with its folder" false "$(lib_names | jq 'index("Fantasma - Film") != null')"
check "and its folder" 0 "$(docker exec "$PREFIX-local" sh -c "ls /config/root/default | grep -c '^Fantasma - Film$'" || true)"
UNBOUND=$(uuidgen | tr A-Z a-z | tr -d -)
ALBA_FILM=$(lib_id "Alba - Film")
DATA=$(docker exec "$PREFIX-local" sh -c 'dirname "$(find /config -name federated-stubs.json | head -1)"')
jq -n --arg old "$(dashed "$GHOST")" --arg unbound "$(dashed "$UNBOUND")" --arg new "$(dashed "$ALBA_FILM")" \
  '{Version: 1, Records: [
    {OldName: "Spettro - Film", NewName: "Alba - Film", OldId: $unbound, NewId: $new, Type: "movies", TemporaryName: null},
    {OldName: "Fantasma - Film", NewName: "Alba - Film", OldId: $old, NewId: $new, Type: "movies", TemporaryName: null}]}' >"$WORK/library-renames.json"
docker cp "$WORK/library-renames.json" "$PREFIX-local:$DATA/library-renames.json"

echo "--- after a restart, with the user blocking an id of before that rename and the unbound id"
uid=$(dashed "$ALL" | tr a-f A-F)
stopped "update Preferences set Value = '$(dashed "$GHOST"),$(dashed "$UNBOUND")' where Kind = 2 and UserId = '$uid';"
want="[\"$ALBA_FILM\",\"$UNBOUND\"]"
for i in $(seq 1 30); do [ "$(blocks)" = "$want" ] && break; sleep 1; done
check "the start finishes the rewrite: the new id is blocked, the unbound id is left alone [rows 290, 329]" "$want" "$(blocks)"
check "and the record is gone" 0 "$(docker exec "$PREFIX-local" sh -c "ls '$DATA' | grep -c '^library-renames.json$'" || true)"
check "the unbound record is set aside [row 329]" 1 "$(docker exec "$PREFIX-local" sh -c "grep -c '$(dashed "$UNBOUND")' '$DATA/library-renames.json.damaged'" || true)"
as all GET "/Items/$(dashed "$(film "Tears of Steel" Alba)")" >/dev/null
check "and cannot open its film" 404 "$(status)"
ghosts "after a restart"
scan local
ghosts "after a restart and a scan"
check "no restart is asked" false "$(get local /JellyMesh/Admin/Status | jq -r .RenamedLibrariesNeedRestart)"
exit "$FAILED"
