#!/usr/bin/env bash
# What Phase 8 asks of a peer beyond boundaries.sh: no path of this server in an answer, an unknown caller learns nothing,
# and how a read without a play session counts. Runs after federate.sh. usage: MEDIA=... WORK=... peer-extra.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0

share a 1 Film Serie
token=$(peer_token a peer)
ordinary=$(peer_token a mario)
as_peer a "$token" GET /JellyMesh/Provider/Libraries >/dev/null
library=$(jq -r '.Items[] | select(.Name == "Film") | .Id' "$WORK/body")
real=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | startswith("/media/"))][0].Id')

echo "--- no path of this server in what a peer reads"
check "items" 200 "$(as_peer a "$token" GET "/JellyMesh/Provider/Items?parentId=$library&includeItemTypes=Movie&limit=500")"
cp "$WORK/body" "$WORK/peer-items"
check "there is something to look at" true "$([ "$(jq '.Items | length' "$WORK/peer-items")" -gt 0 ] && echo true || echo false)"
check "no path of this server" 0 "$(grep -c -E '"/(media|config|cache|extra|federata)/' "$WORK/peer-items")"
check "no key named like a path has a value" 0 "$(jq '[.. | objects | to_entries[] | select((.key | test("Path$")) and .value != null)] | length' "$WORK/peer-items")"
check "the name of the local server is not there" 0 "$(jq '[.. | objects | select(has("ServerName"))] | length' "$WORK/peer-items")"

echo "--- an answer of a closed route has no body worth reading"
check "a closed route" 403 "$(as_peer a "$token" GET /System/Info)"
echo "body of a 403: $(head -c 200 "$WORK/body")"
check "a body holds no path" 0 "$(grep -c -E '/(media|config|cache)/' "$WORK/body")"

echo "--- who is not a peer"
check "an ordinary user on the provider api" 403 "$(as_peer a "$ordinary" GET /JellyMesh/Provider/Libraries)"
check "an ordinary user gets the 404 of a stock server" 404 "$(as_peer a "$ordinary" GET /JellyMesh/Capabilities)"
check "an unknown token" 401 "$(as_peer a nonsense GET /JellyMesh/Provider/Libraries)"
check "nobody" 401 "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18410/JellyMesh/Provider/Libraries)"
check "a peer does not reach the administration" 403 "$(as_peer a "$token" GET /JellyMesh/Admin/Peers)"
check "a peer does not reach a playback of a client" 403 "$(as_peer a "$token" GET "/JellyMesh/Playback/$(printf '0%.0s' {1..32})/master.m3u8")"

echo "--- a read with no play session (decision 18): limit 1"
# A consumer that timed out never learned the play session, so its slot goes by itself, after 120 s.
for i in $(seq 1 130); do
  [ "$(active_streams)" = 0 ] && break
  sleep 1
done
stream="http://127.0.0.1:18410/Videos/$real/stream?static=true"
check "a probe of one byte" 206 "$(curl -sS -o /dev/null -w '%{http_code}' -r 0-0 -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\"" "$stream")"
check "the probe holds nothing afterwards" 0 "$(active_streams)"
curl -sS -o /dev/null --limit-rate 50k --max-time 12 -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\"" "$stream" &
reader=$!
sleep 2
check "the read holds the one slot" 1 "$(active_streams)"
check "a second read is refused" 429 "$(curl -sS -o /dev/null -w '%{http_code}' -r 0-0 -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\"" "$stream")"
check "a PlaybackInfo is refused" 429 "$(as_peer a "$token" POST "/Items/$real/PlaybackInfo")"
check "the count never passed the limit" 1 "$(active_streams)"
kill "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
sleep 1
check "the slot is free when the read ends" 0 "$(active_streams)"

exit "$FAILED"
