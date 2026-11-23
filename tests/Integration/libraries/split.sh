#!/usr/bin/env bash
# F44b (§10 rows 439, 497 and 507, D58, D66): a title that one server has in two of its libraries. Per type, both folders
# are in the same local library, so one stub is visible and the other copy is its internal source (D19). Per remote
# library each library lists its own stub, and the new one takes the whole watch state: position, play count, favourite,
# played, of each user. With the single library on, the two stubs are versions of one title
# there and stay in the libraries of the server (D29). Back to per type, one stub is left with the state of both.
# A stock remote with two film libraries and two series libraries that share titles, and a consumer with the plugin.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-f44b] [PORT=18990] [KEEP=1] split.sh
set -euo pipefail
PREFIX=${PREFIX:-jm-f44b}
PORT=${PORT:-18990}
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"
trap '[ "${KEEP:-0}" = 1 ] || down' EXIT

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
SEEN_FILM="JM Confronto Risoluzione"
HALF_FILM="JM Confronto Bitrate"
EPISODE="Serie Divisa S01E05"
own() { get local /JellyMesh/Admin/Status | jq -c --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .AutomaticLibraries'; }
shared() { get local /JellyMesh/Admin/SharedLibraries; }
id_of() { get local /Library/VirtualFolders | jq -r --arg n "$1" '.[] | select(.Name == $n) | .ItemId'; }
wait_busy() { # wait_busy <command printing the state>: until its Busy is false
  local i
  for i in $(seq 1 300); do
    [ "$($1 | jq -r .Busy)" = false ] && return 0
    sleep 1
  done
  echo "the libraries job did not end" >&2
  return 1
}
mode() { # mode <library mode>: changes the mode of the server, as the Edit form saves it
  req local PUT "/JellyMesh/Admin/Servers/$SERVER" "$(jq -nc --arg u "$REMOTE_URL" --arg m "$1" '{Name:"Casa",Url:$u,Username:"jellymesh",Password:"",LibraryMode:$m}')" >/dev/null
  check "the change to $1 answers 200" 200 "$(status)"
  wait_busy own
}
# in_library <library> <type> <text> [user id]: the ids of the items whose path has the text, in one library.
in_library() {
  get local "/Items?userId=${4:-$ME}&ParentId=$(id_of "$1")&Recursive=true&IncludeItemTypes=$2&Fields=Path" |
    jq -r --arg t "$3" '[.Items[] | select((.Path // "") | contains($t)) | .Id] | sort | join(" ")'
}
versions() { # versions <library> <title>: how many items of the title it lists and how many versions the first has
  get local "/Items?userId=$ME&ParentId=$(id_of "$1")&Recursive=true&IncludeItemTypes=Movie&Fields=MediaSources" |
    jq -r --arg t "$2" '[.Items[] | select(.Name | contains($t))] | "\(length) \(.[0].MediaSources // [] | length)"'
}
# data <item id> [user id]: the state of a user on an item.
data() { get local "/UserItems/$1/UserData?userId=${2:-$ME}" | jq -r '"\(.Played) \(.PlayCount) \(.IsFavorite) \(.PlaybackPositionTicks)"'; }
mark() { post local "/UserItems/$1/UserData?userId=${3:-$ME}" "$2" >/dev/null; }
# strm <text>: how many stubs of a title are on disk.
strm() { docker exec "$PREFIX-local" sh -c "find /federata -name '*.strm' | grep -cF '$1'" || true; }
# until_is <expected> <command...>: syncs and scans until the command prints the expected text, at most 6 times.
until_is() {
  local want=$1 i
  shift
  for i in 1 2 3 4 5 6; do
    [ "$("$@")" = "$want" ] && return 0
    sync_now local
    scan local
  done
  return 0
}
# stubs: how many stubs of the film, the half seen film and the episode are on disk.
stubs() { echo "$(strm "$SEEN_FILM") $(strm "$HALF_FILM") $(strm "$EPISODE")"; }
# placed: how many of them each library of the server lists.
count() { in_library "$@" | wc -w | tr -d ' '; }
placed() { echo "$(count 'Casa - Film HD' Movie "$SEEN_FILM") $(count 'Casa - Film Bambini' Movie "$SEEN_FILM") $(count 'Casa - Telefilm HD' Episode "$EPISODE") $(count 'Casa - Telefilm Bambini' Episode "$EPISODE")"; }

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
START_ENV="-v $MEDIA/remoto-jellymesh:/bambini:ro" start remote "$MEDIA/remoto-standard:/media:ro"
library remote "Film HD" movies /media/film
library remote "Film Bambini" movies /bambini/film
library remote "Telefilm HD" tvshows /media/serie
library remote "Telefilm Bambini" tvshows /bambini/serie
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
wait_busy own
scan local

echo "--- per type: one visible stub, the other copy is its internal source [D19]"
until_is "1 1 1" stubs
check "a film or an episode in two libraries of the server has one stub" "1 1 1" "$(stubs)"
SEEN=$(in_library 'Casa - Film' Movie "$SEEN_FILM")
HALF=$(in_library 'Casa - Film' Movie "$HALF_FILM")
EP=$(in_library 'Casa - Serie' Episode "$EPISODE")
check "each is one item of the library" "1 1 1" "$(wc -w <<<"$SEEN" | tr -d ' ') $(wc -w <<<"$HALF" | tr -d ' ') $(wc -w <<<"$EP" | tr -d ' ')"
mark "$SEEN" '{"Played":true,"PlayCount":3,"IsFavorite":true,"LastPlayedDate":"2026-11-01T20:00:00Z"}'
mark "$HALF" '{"PlaybackPositionTicks":600000000,"PlayCount":1,"IsFavorite":true,"LastPlayedDate":"2026-11-02T20:00:00Z"}'
mark "$EP" '{"PlaybackPositionTicks":300000000,"PlayCount":1,"LastPlayedDate":"2026-11-03T20:00:00Z"}'
OTHER=$(user local altro)
mark "$SEEN" '{"Played":true,"PlayCount":1,"LastPlayedDate":"2026-11-04T20:00:00Z"}' "$OTHER"
STATES="$(data "$SEEN") / $(data "$HALF") / $(data "$EP")"
check "the state is set" "true 3 true 0 / false 1 true 600000000 / false 1 false 300000000" "$STATES"

echo "--- per remote library: one stub in each library, with the whole state [rows 439 and 507 c]"
mode ByRemoteLibrary
until_is "1 1 1 1" placed
check "the film and the episode are in both libraries of their type" "1 1 1 1" "$(placed)"
check "each has a stub in each remote library folder" "2 2 2" "$(stubs)"
SEEN_HD=$(in_library 'Casa - Film HD' Movie "$SEEN_FILM")
SEEN_KIDS=$(in_library 'Casa - Film Bambini' Movie "$SEEN_FILM")
HALF_HD=$(in_library 'Casa - Film HD' Movie "$HALF_FILM")
HALF_KIDS=$(in_library 'Casa - Film Bambini' Movie "$HALF_FILM")
EP_HD=$(in_library 'Casa - Telefilm HD' Episode "$EPISODE")
EP_KIDS=$(in_library 'Casa - Telefilm Bambini' Episode "$EPISODE")
check "the old items keep their ids" "yes yes yes" "$([[ " $SEEN_HD $SEEN_KIDS " = *" $SEEN "* ]] && echo yes) $([[ " $HALF_HD $HALF_KIDS " = *" $HALF "* ]] && echo yes) $([[ " $EP_HD $EP_KIDS " = *" $EP "* ]] && echo yes)"
states_hd() { echo "$(data "$SEEN_HD") / $(data "$HALF_HD") / $(data "$EP_HD")"; }
states_kids() { echo "$(data "$SEEN_KIDS") / $(data "$HALF_KIDS") / $(data "$EP_KIDS")"; }
until_is "$STATES" states_hd
until_is "$STATES" states_kids
check "the stubs of the HD libraries have the whole state" "$STATES" "$(states_hd)"
check "the stubs of the children libraries have the whole state" "$STATES" "$(states_kids)"
check "another user has their own" "true 1 false 0 / true 1 false 0" "$(data "$SEEN_HD" "$OTHER") / $(data "$SEEN_KIDS" "$OTHER")"
BEFORE="$SEEN_HD $SEEN_KIDS $HALF_HD $HALF_KIDS $EP_HD $EP_KIDS"
sync_now local
scan local
now_ids() { echo "$(in_library 'Casa - Film HD' Movie "$SEEN_FILM") $(in_library 'Casa - Film Bambini' Movie "$SEEN_FILM") $(in_library 'Casa - Film HD' Movie "$HALF_FILM") $(in_library 'Casa - Film Bambini' Movie "$HALF_FILM") $(in_library 'Casa - Telefilm HD' Episode "$EPISODE") $(in_library 'Casa - Telefilm Bambini' Episode "$EPISODE")"; }
check "another sync and scan leave every stub where it is" "$BEFORE" "$(now_ids)"
check "the copies have their own state from then on" "false 1 true 600000000 / false 1 true 900000000" \
  "$(mark "$HALF_KIDS" '{"PlaybackPositionTicks":900000000,"PlayCount":1,"IsFavorite":true,"LastPlayedDate":"2026-11-10T20:00:00Z"}'; echo "$(data "$HALF_HD") / $(data "$HALF_KIDS")")"
sync_now local
scan local
check "and a sync and a scan do not copy it again" "false 1 true 600000000 / false 1 true 900000000" "$(data "$HALF_HD") / $(data "$HALF_KIDS")"

echo "--- with the single library: versions of one title there, still one in each library of the server [row 507 a, D29]"
post local /JellyMesh/Admin/SharedLibraries '{"Enabled":true}' >/dev/null
check "the switch-on answers 202" 202 "$(status)"
wait_busy shared
scan local
shown() { versions "Tutti i server - Film" "$SEEN_FILM"; }
until_is "1 2" shown
check "the single library lists the film once with two versions" "1 2" "$(shown)"
check "each library of the server still lists it" "1 1 1 1" "$(placed)"
check "the film is played in both" "true true" "$(data "$SEEN_HD" | cut -d' ' -f1) $(data "$SEEN_KIDS" | cut -d' ' -f1)"
answer=$(post local /JellyMesh/Admin/SharedLibraries '{"Enabled":false}')
post local /JellyMesh/Admin/SharedLibraries "$(echo "$answer" | jq -c '{Enabled: false, Confirmed: true, ToDelete, ToRelease}')" >/dev/null
wait_busy shared
scan local
check "with the single library off the libraries of the server list it as before" "1 1 1 1" "$(placed)"

echo "--- back to per type: one stub is left, with the state of both [rows 63, 69 and 439]"
mode ByType
until_is "1 1 1" stubs
check "each title has one stub again" "1 1 1" "$(stubs)"
one() { echo "$(count 'Casa - Film' Movie "$SEEN_FILM") $(count 'Casa - Film' Movie "$HALF_FILM") $(count 'Casa - Serie' Episode "$EPISODE")"; }
until_is "1 1 1" one
check "each is one item of the library again" "1 1 1" "$(one)"
check "the state of both copies is on it, the newest position included" "true 3 true 0 / false 1 true 900000000 / false 1 false 300000000" \
  "$(data "$(in_library 'Casa - Film' Movie "$SEEN_FILM")") / $(data "$(in_library 'Casa - Film' Movie "$HALF_FILM")") / $(data "$(in_library 'Casa - Serie' Episode "$EPISODE")")"

[ "$FAILED" = 0 ] && echo "all passed" || { echo "some checks failed"; exit 1; }
