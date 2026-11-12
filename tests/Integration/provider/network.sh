#!/usr/bin/env bash
# A slow and an offline provider, through the gate. Runs after limits.sh, which adds the one-source title.
# usage: MEDIA=... WORK=... network.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
SOLO="JM Solo Provider"

share a 3 Film Serie
gate_mode a up
gate_mode stock up
tears=$(movie_on_b "Tears of Steel")
solo=$(movie_on_b "$SOLO")
server=$(cat "$WORK/id-b-from-a")

timed_open() { # <device> <item>: prints the status; the seconds go to $WORK/seconds
  local started code
  started=$(date +%s)
  code=$(open_stream "$1" "$2" "$PROFILE_DIRECT")
  echo $(($(date +%s) - started)) >"$WORK/seconds"
  echo "$code"
}

echo "--- slow network: still served by a"
gate_mode a slow:800
: >"$WORK/gate-a.log"
: >"$WORK/gate-stock.log"
check "open" 200 "$(timed_open n1 "$tears")"
echo "took $(cat "$WORK/seconds") s"
check "from a" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-a.log")"
check "the stock remote was not needed" 0 "$(grep -c PlaybackInfo "$WORK/gate-stock.log")"
sync_now b
check "a sync works too" null "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"
close_stream n1

echo "--- too slow: past the timeout the next source serves"
gate_mode a slow:12000
: >"$WORK/gate-stock.log"
check "open" 200 "$(timed_open n2 "$tears")"
echo "took $(cat "$WORK/seconds") s"
check "within the timeout of the source" true "$([ "$(cat "$WORK/seconds")" -le 14 ] && echo true || echo false)"
check "from the stock remote" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-stock.log")"
close_stream n2
gate_mode a up

echo "--- offline, a source that answers nothing at all"
gate_mode a hang
: >"$WORK/gate-stock.log"
check "open" 200 "$(timed_open n3 "$tears")"
echo "took $(cat "$WORK/seconds") s"
check "from the stock remote" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-stock.log")"
close_stream n3

echo "--- offline, connection refused"
gate_mode a down
: >"$WORK/gate-stock.log"
check "open" 200 "$(timed_open n4 "$tears")"
check "from the stock remote" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-stock.log")"
close_stream n4
solo_open() { open_stream n5 "$solo" "$PROFILE_DIRECT"; }
message=$(message_during n5 8 solo_open)
check "with no other source" 503 "$(cat "$WORK/during-n5")"
check "the offline message" "$OFFLINE_TEXT" "$message"
sync_now b
check "the sync marks it offline" true "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .IsOffline')"
check "its stubs stay" 1 "$(docker exec "$PREFIX-b" sh -c "find /federata -name '$SOLO*.strm' | wc -l" | tr -d ' ')"

echo "--- back online"
gate_mode a up
sync_now b
check "the sync recovers" null "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"
check "and the mode is still JellyMesh" JellyMesh "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$server" '.Servers[] | select(.Id == $i) | .Mode')"
check "open" 200 "$(timed_open n6 "$solo")"
close_stream n6

echo "--- nothing secret in the logs"
for s in a b; do
  check "no address of a remote in the log of $s" 0 "$(docker logs "$PREFIX-$s" 2>&1 | grep -c -E "host\.docker\.internal|:($GATE_A|$GATE_B|$GATE_STOCK)")"
done

exit "$FAILED"
