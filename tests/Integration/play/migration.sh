#!/usr/bin/env bash
# Test 8 of the spec (Q40, Q47, Q49, Q55-Q57, D12, D15): a flat layout made by a 0.4.x that records the last server
# that served each user, an upgrade, the banner and its choice, the old stubs working while the new paths are not linked,
# then the paths added, the scan, the state carried as Q47 says and the old folders emptied down to their .jellymesh.
# MODE=manual: the admin adds and removes the paths. MODE=automatic: the plugin does, after a failed step and a restart.
#   local  $PREFIX-local  $PORT      plugin, consumer of a and b, users alice and bob
#   a, b   $PREFIX-a/-b   $PORT+1/2  stock Jellyfin with the media of remoto-standard
# OLD is a checkout of a 0.4.x with the last-served record (0.4.2.0 or later), made with archive and tar.
# usage: MEDIA=<main checkout>/tests/Integration/media OLD=<folder> [MODE=manual|automatic] [PREFIX=jm-migration] [PORT=18720] migration.sh [--keep]
set -euo pipefail
MODE=${MODE:-manual}
PREFIX=${PREFIX:-jm-migration}
PORT=${PORT:-18720}
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"
OLD=${OLD:?a checkout of a 0.4.x with the last-served record}

WORK=${WORK_MIGRATION:-${TMPDIR:-/tmp}/$PREFIX-run}
mkdir -p "$WORK"
name_of() { case $1 in alice | bob) echo "$PREFIX-local" ;; *) echo "$PREFIX-$1" ;; esac; }
port_of() { case $1 in local | alice | bob) echo "$PORT" ;; a) echo $((PORT + 1)) ;; b) echo $((PORT + 2)) ;; esac; }
# vid_any <path>: the N-format id of the item with exactly that path, of any type.
vid_any() { sql "select lower(replace(Id, '-', '')) from BaseItems where Path = '$1' limit 1;"; }

down_migration() {
  docker rm -f "$PREFIX-local" "$PREFIX-a" "$PREFIX-b" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" "$PREFIX-local-media" \
    "$PREFIX-a-config" "$PREFIX-a-cache" "$PREFIX-a-fed" "$PREFIX-b-config" "$PREFIX-b-cache" "$PREFIX-b-fed" >/dev/null 2>&1 || true
}
[ "${1:-}" = --keep ] || trap down_migration EXIT
down_migration

for r in a b; do
  start "$r" "$MEDIA/remoto-standard:/media:ro"
  library "$r" Film movies /media/film
  library "$r" Serie tvshows /media/serie
  user "$r" jellymesh >/dev/null
  scan "$r"
done

# The flat layout, as a 0.4.x leaves it: the old paths in the libraries.
start local "$PREFIX-local-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film /federata/Film
library local Serie tvshows /federata/Serie
user local alice >/dev/null
user local bob >/dev/null
NUGET_VOLUME=jellymesh-nuget bash "$OLD/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id_a=$(add_remote local a "http://host.docker.internal:$(port_of a)" jellymesh)
id_b=$(add_remote local b "http://host.docker.internal:$(port_of b)" jellymesh)
import_all local "$id_a"
import_all local "$id_b"
sync_now local
scan local
# A scan queued by the sync can end first: scan again until the flat film and a flat series are in the library.
for i in 1 2 3 4 5; do
  [ -n "$(vid "/federata/Film/Tears of Steel")" ] && [ -n "$(sql "select Path from BaseItems where Type like '%.Series' and Path like '/federata/Serie/%' limit 1;")" ] && break
  sleep 10
  scan local
done

winner=$(get local /JellyMesh/Admin/Index/Decisions | jq -r '[.Contents[] | select((.Backups // []) | length > 0) | select(.Identity.Key | test("tears|2285752|133701"; "i"))][0].Winner.ServerName // ""')
case $winner in a) backup=b ;; b) backup=a ;; *) echo "no winner with a backup: [$winner]" >&2; exit 1 ;; esac
id_of() { case $1 in a) echo "$id_a" ;; b) echo "$id_b" ;; esac; }
echo "winner $winner, backup $backup"

old_tears=$(vid "/federata/Film/Tears of Steel")
old_series_path=$(sql "select Path from BaseItems where Type like '%.Series' and Path like '/federata/Serie/%' order by Path limit 1;")
old_series=$(vid_any "$old_series_path")
series_name=${old_series_path#/federata/Serie/}
check "the flat film is a stub" 1 "$([ -n "$old_tears" ] && echo 1 || echo 0)"
check "the flat series is there" 1 "$([ -n "$old_series" ] && echo 1 || echo 0)"
flat_count=$(docker exec "$PREFIX-local" sh -c "find /federata/Film /federata/Serie -name '*.strm' | wc -l" | tr -d ' ')
echo "flat stubs: $flat_count, series: $series_name"

uid_alice=$(user_id local alice)
uid_bob=$(user_id local bob)
data() { get local "/UserItems/$(dashed "$2")/UserData?userId=$1"; }
set_data() { post local "/UserItems/$(dashed "$2")/UserData?userId=$1" "$3" >/dev/null; status; }
play() { # play <user> <item>: a PlaybackInfo as that user; prints the number of sources
  login "$1" "$1"
  post "$1" "/Items/$(dashed "$2")/PlaybackInfo?UserId=$(user_id local "$1")&AutoOpenLiveStream=false" "$DIRECT" | jq -r '.MediaSources | length'
}

# Alice is served by the backup, so the 0.4.x records it; Bob is not served at all.
docker stop "$PREFIX-$winner" >/dev/null
check "alice is served by the backup on the flat stub" 1 "$(play alice "$old_tears")"
docker start "$PREFIX-$winner" >/dev/null
wait_ready "$winner"
login local
check "alice has a position" 200 "$(set_data "$uid_alice" "$old_tears" '{"PlaybackPositionTicks":300000000}')"
check "bob has a position and a favorite" 200 "$(set_data "$uid_bob" "$old_tears" '{"PlaybackPositionTicks":200000000,"IsFavorite":true,"Rating":8}')"
check "bob likes the series" 200 "$(set_data "$uid_bob" "$old_series" '{"IsFavorite":true}')"

# The upgrade writes the server folders beside the flat ones.
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
sync_now local
scan local
migration() { get local /JellyMesh/Admin/Status | jq -c '.FederatedFolder.Migration'; }
step() { migration | jq -r '.Step // "none"'; }
choose() { post local /JellyMesh/Admin/Migration "{\"Mode\":\"$1\"}" >/dev/null; status; }
stubs() { docker exec "$PREFIX-local" sh -c "find /federata/Film /federata/Serie -name '*.strm' | wc -l" | tr -d ' '; }

# Q49 and D12: the new paths are not linked, so the old stubs stay, play and stay out of the groups.
migration
check "the banner asks for the new paths" AddPaths "$(step)"
check "and for the choice (Q55)" None "$(migration | jq -r .Mode)"
check "it lists the libraries on the old paths" "Film,Serie" "$(migration | jq -r '[.Libraries[].Name] | sort | join(",")')"
check "with the folders of both servers to add" 4 "$(migration | jq '[.Libraries[].MissingPaths[]] | length')"
check "the old stubs stay" "$flat_count" "$(stubs)"
check "the old film still plays" 1 "$(play bob "$old_tears")"
login local
check "and is in no version group" ":" "$(primary_of "$old_tears"):$(alternates_of "$old_tears")"
check "the state stays on the old film" "200000000 true" "$(data "$uid_bob" "$old_tears" | jq -r '"\(.PlaybackPositionTicks) \(.IsFavorite)"')"
check "a later scan changes nothing" AddPaths "$(scan local && step)"
check "the old stubs are still there" "$flat_count" "$(stubs)"

verify_carried() {
  tears_of() { stub_of "$1" "Tears of Steel"; }
  tears_w=$(tears_of "$(id_of "$winner")")
  tears_b=$(tears_of "$(id_of "$backup")")
  primary=$tears_w
  [ -n "$(primary_of "$tears_w")" ] && primary=$tears_b
  echo "winner stub $tears_w, backup stub $tears_b, primary $primary"
  links="$(primary_of "$tears_w")|$(primary_of "$tears_b")"
  check "the two new stubs are linked as one title" 1 "$([ "$links" = "|$tears_w" ] || [ "$links" = "$tears_b|" ] && echo 1 || echo 0)"
  check "alice's position lands on the server that served her (D15)" 300000000 "$(data "$uid_alice" "$tears_b" | jq -r .PlaybackPositionTicks)"
  check "bob's position lands on the best version (Q47)" 200000000 "$(data "$uid_bob" "$tears_w" | jq -r .PlaybackPositionTicks)"
  check "bob's favorite and rating land on the primary (Q47)" "true 8" "$(data "$uid_bob" "$primary" | jq -r '"\(.IsFavorite) \(.Rating)"')"
  for id in "$id_a" "$id_b"; do
    series=$(vid_any "$(server_folder "$id")/Serie/$series_name")
    check "each server has the new series" 1 "$([ -n "$series" ] && echo 1 || echo 0)"
    [ -n "$series" ] && check "bob's series favorite lands on the series of each server" true "$(data "$uid_bob" "$series" | jq -r .IsFavorite)"
  done
  check "the old folders are empty" 0 "$(docker exec "$PREFIX-local" sh -c "find /federata/Film /federata/Serie -mindepth 1 ! -name .jellymesh | wc -l" | tr -d ' ')"
  check "their .jellymesh stay" 2 "$(docker exec "$PREFIX-local" sh -c "ls /federata/Film/.jellymesh /federata/Serie/.jellymesh | wc -l" | tr -d ' ')"
  check "the old items are gone from the library" 0 "$(sql "select count(*) from BaseItems where Path like '/federata/Film/%' or Path like '/federata/Serie/%';")"
}

if [ "$MODE" = manual ]; then
  # The admin adds the new paths: the scan links the versions and the state goes over.
  check "the admin does it by hand" 200 "$(choose Manual)"
  for id in "$id_a" "$id_b"; do
    check "the Film folder joins" 204 "$(link_folder local Film "$(server_folder "$id")/Film")"
    check "the Serie folder joins" 204 "$(link_folder local Serie "$(server_folder "$id")/Serie")"
  done
  check "the banner waits for the scan" Carrying "$(step)"
  for i in 1 2 3 4 5; do
    scan local
    [ "$(step)" = RemoveOldPaths ] && break
  done
  migration
  verify_carried
  check "the banner asks to remove the old paths" RemoveOldPaths "$(step)"

  for library in Film Serie; do
    check "the old path leaves $library" 204 "$(unlink_folder local "$library" "/federata/$library")"
  done
  check "the banner is gone" none "$(step)"
else
  # Q56: a step that cannot be done stops the run with the library named, and nothing else changes.
  b_serie="$(server_folder "$id_b")/Serie"
  docker exec "$PREFIX-local" sh -c "mv '$b_serie' /tmp/serie-away && touch '$b_serie'"
  check "the plugin does it" 202 "$(choose Automatic)"
  wait_run() { # wait_run: until the run stops, scanning when it waits at the carry for the new stubs
    local i now
    for i in $(seq 1 90); do
      now=$(migration)
      [ "$now" = null ] && return 0
      if [ "$(jq -r .Running <<<"$now")" = false ]; then
        [ "$(jq -r .Error <<<"$now")" != None ] && return 0
        [ "$(jq -r .AutomaticStep <<<"$now")" = Carry ] && scan local
      fi
      sleep 5
    done
    echo "the automatic migration did not stop" >&2
    return 1
  }
  wait_run
  migration
  check "the run stops on the library that failed" "AddFailed Serie AddPaths false" "$(migration | jq -r '"\(.Error) \(.ErrorLibrary) \(.AutomaticStep) \(.Running)"')"
  locations() { get local /Library/VirtualFolders | jq -r --arg n "$1" '.[] | select(.Name == $n) | .Locations | sort | join(",")'; }
  check "the old paths stay in Film" 1 "$(locations Film | tr ',' '\n' | grep -cx /federata/Film)"
  check "and in Serie" 1 "$(locations Serie | tr ',' '\n' | grep -cx /federata/Serie)"
  check "the old stubs stay" "$flat_count" "$(stubs)"
  check "the old film still plays" 1 "$(play bob "$old_tears")"
  login local
  check "the run cannot go back to manual" 409 "$(choose Manual)"
  docker restart "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
  check "the stop survives a restart (Q57)" "Automatic AddFailed Serie" "$(migration | jq -r '"\(.Mode) \(.Error) \(.ErrorLibrary)"')"

  # The admin fixes the folder and asks again: the run goes on from the saved step to the end.
  docker exec "$PREFIX-local" sh -c "rm '$b_serie' && mv /tmp/serie-away '$b_serie'"
  check "the run starts again" 202 "$(choose Automatic)"
  wait_run
  check "the banner is gone" null "$(migration)"
  verify_carried
  check "Film holds the local media and the Film folders of the servers" \
    "$(printf '%s\n' /media/film "$(server_folder "$id_a")/Film" "$(server_folder "$id_b")/Film" | sort | paste -sd, -)" "$(locations Film)"
  check "Serie holds the Serie folders of the servers" \
    "$(printf '%s\n' "$(server_folder "$id_a")/Serie" "$(server_folder "$id_b")/Serie" | sort | paste -sd, -)" "$(locations Serie)"
fi
check "the status names no remote address" 0 "$(get local /JellyMesh/Admin/Status | jq -c '.FederatedFolder' | grep -c "host.docker.internal" || true)"
exit "$FAILED"
