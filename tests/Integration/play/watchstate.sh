#!/usr/bin/env bash
# Decision 41: watch state stays on each server and for each user. A stock remote and a consumer with the current build.
# Watching on the consumer must not change any user's state on the remote, and the reverse.
# usage: MEDIA=<main checkout>/tests/Integration/media watchstate.sh   (leaves the containers up; lib.sh has down)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

down
docker volume create "$PREFIX-media" >/dev/null

start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
user remote viewer >/dev/null
scan remote

start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film /federata/Film
user local viewer >/dev/null
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
import_all local "$id"
sync_now local
scan local

# remote_state: the watch state of every remote user on every film, as one sorted line per user and film.
remote_state() {
  local name uid
  for name in admin jellymesh viewer; do
    uid=$(user_id remote "$name")
    get remote "/Items?userId=$uid&Recursive=true&IncludeItemTypes=Movie" |
      jq -r --arg u "$name" '.Items[] | "\($u) \(.Name) played=\(.UserData.Played) count=\(.UserData.PlayCount) pos=\(.UserData.PlaybackPositionTicks)"'
  done | sort
}

# local_state <item>: the watch state of the viewer and the admin on one consumer item.
local_state() {
  local name uid
  for name in admin viewer; do
    uid=$(user_id local "$name")
    get local "/Items?userId=$uid&Ids=$1" | jq -r --arg u "$name" '.Items[0] | "\($u) played=\(.UserData.Played) count=\(.UserData.PlayCount) pos=\(.UserData.PlaybackPositionTicks)"'
  done
}

stubs=$(get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select((.Path // "") | startswith("/federata"))]')
check "the consumer has at least three stubs" true "$(echo "$stubs" | jq 'length >= 3')"
watched=$(echo "$stubs" | jq -r '.[0].Id')
paused=$(echo "$stubs" | jq -r '.[1].Id')
remote_marked=$(echo "$stubs" | jq -r '.[2].Id')
remote_marked_name=$(echo "$stubs" | jq -r '.[2].Name')

before=$(remote_state)
check "nothing is watched on the remote yet" 0 "$(echo "$before" | grep -vc 'played=false count=0 pos=0' || true)"

# On the consumer: the viewer streams one film to the end and stops another halfway, through the stock routes.
viewer_login
runtime=$(as_viewer GET "/Items/$watched" >/dev/null; jq -r .RunTimeTicks "$WORK/viewer-body")
bytes=$(curl -sS -o /dev/null -w '%{size_download}' "http://127.0.0.1:$PORT/Videos/$watched/stream?static=true&ApiKey=$(cat "$WORK/token-viewer")")
check "the whole film streams through the consumer" true "$([ "$bytes" -gt 100000 ] && echo true || echo false)"
as_viewer POST /Sessions/Playing "{\"ItemId\":\"$watched\",\"PlaySessionId\":\"ws-1\",\"PositionTicks\":0}" >/dev/null
as_viewer POST /Sessions/Playing/Stopped "{\"ItemId\":\"$watched\",\"PlaySessionId\":\"ws-1\",\"PositionTicks\":$runtime}" >/dev/null
check "the film is watched on the consumer" true "$(local_state "$watched" | grep '^viewer' | grep -q 'played=true' && echo true || echo false)"

half=$(as_viewer GET "/Items/$paused" >/dev/null; jq -r '.RunTimeTicks / 2 | floor' "$WORK/viewer-body")
curl -sS -o /dev/null -H 'Range: bytes=0-999999' "http://127.0.0.1:$PORT/Videos/$paused/stream?static=true&ApiKey=$(cat "$WORK/token-viewer")"
as_viewer POST /Sessions/Playing "{\"ItemId\":\"$paused\",\"PlaySessionId\":\"ws-2\",\"PositionTicks\":0}" >/dev/null
as_viewer POST /Sessions/Playing/Stopped "{\"ItemId\":\"$paused\",\"PlaySessionId\":\"ws-2\",\"PositionTicks\":$half}" >/dev/null
# The clips are shorter than the 300 s Jellyfin needs to keep a resume point, so stopping halfway marks them played.
check "stopping the other film leaves state on the consumer" true "$(local_state "$paused" | grep '^viewer' | grep -vq 'played=false count=0 pos=0' && echo true || echo false)"
as_viewer POST "/UserPlayedItems/$watched" >/dev/null

sleep 5
check "no remote user's watch state changed" "$before" "$(remote_state)"

# On the remote: the dedicated user and a remote viewer watch a film; the consumer syncs and must not take it.
rid=$(get remote '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -r --arg n "$remote_marked_name" '[.Items[] | select(.Name == $n)][0].Id')
for name in jellymesh viewer; do
  login remote "$name"
  post remote "/UserPlayedItems/$rid" >/dev/null
done
login remote
check "the dedicated user and the remote viewer have it watched" 2 "$(remote_state | grep -F " $remote_marked_name " | grep -c 'played=true')"
local_before=$(local_state "$remote_marked")
sync_now local
scan local
check "the consumer did not take the remote watch state" "$local_before" "$(local_state "$remote_marked")"
check "and nobody on the consumer has it watched" 0 "$(local_state "$remote_marked" | grep -vc 'played=false count=0 pos=0' || true)"

echo "=== failed: $FAILED"
exit $FAILED
