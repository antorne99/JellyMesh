#!/usr/bin/env bash
# The stream limit: b plays from a, with a limit of 1 for it. Over the limit b passes to the next source, to the local
# fallback, or to the 503, and a slot never leaks. Runs after federate.sh, and takes about 8 minutes because it waits for
# real timeouts. usage: MEDIA=... WORK=... limits.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
SOLO="JM Solo Provider"

share a 1 Film Serie
gate_mode a up
gate_mode stock up

docker exec jm-prov-a sh -c "mkdir -p '/extra/film/$SOLO (2026)' && cp \"\$(find '/media/film/Tears of Steel (2012)' -type f -name '*.m*' | head -1)\" '/extra/film/$SOLO (2026)/$SOLO (2026).mkv'"
scan a
sync_now b
scan b
tears=$(movie_on_b "Tears of Steel")
solo=$(movie_on_b "$SOLO")
echo "two sources: $tears, one source: $solo"
token=$(peer_token a peer)
real=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | startswith("/media/"))][0].Id')

echo "--- over the limit: the next source"
: >"$WORK/gate-a.log"
: >"$WORK/gate-stock.log"
check "first stream" 200 "$(open_stream c1 "$tears" "$PROFILE_DIRECT")"
check "it holds the slot on a" 1 "$(active_streams)"
check "it came from a" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-a.log")"
check "second stream" 200 "$(open_stream c2 "$tears" "$PROFILE_DIRECT")"
check "a refused it" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 429' "$WORK/gate-a.log")"
check "the stock remote served it" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-stock.log")"
check "a still holds one slot" 1 "$(active_streams)"
check "b logged the limit" true "$(docker logs jm-prov-b 2>&1 | grep -q 'is at its stream limit' && echo true || echo false)"
check "a client sees no remote address" 0 "$(cat "$WORK/open-c1" "$WORK/open-c2" | grep -c -E 'host\.docker\.internal|18412|18414')"
close_stream c2
close_stream c1
check "closing frees the slot at once" 0 "$(active_streams)"

echo "--- the provider enforces it, whatever the consumer does"
for i in 1 2 3 4 5; do
  curl -sS -o "$WORK/race-$i" -w '%{http_code}\n' -D "$WORK/race-head-$i" -X POST -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\"" \
    "http://127.0.0.1:18410/Items/$real/PlaybackInfo" >"$WORK/race-code-$i" &
done
wait
check "five at once: one passes" 1 "$(cat "$WORK"/race-code-* | grep -c 200)"
check "five at once: four refused" 4 "$(cat "$WORK"/race-code-* | grep -c 429)"
check "the refusal carries the signal" 4 "$(cat "$WORK"/race-head-* | grep -c -i 'x-jellymesh-refusal: stream-limit')"
check "a stream without a slot is refused" 429 "$(as_peer a "$token" GET "/Videos/$real/stream?static=true")"
check "HLS without a play session is refused" 403 "$(as_peer a "$token" GET "/Videos/$real/master.m3u8?MediaSourceId=$real")"
for i in 1 2 3 4 5; do
  session=$(jq -r '.PlaySessionId // empty' "$WORK/race-$i" 2>/dev/null)
  [ -n "$session" ] && as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$session" >/dev/null
done
check "free again" 0 "$(active_streams)"

echo "--- over the limit with one source: the 503 with its own message"
check "hold the slot" 200 "$(open_stream c1 "$tears" "$PROFILE_DIRECT")"
: >"$WORK/gate-a.log"
solo_open() { open_stream c3 "$solo" "$PROFILE_DIRECT"; }
message=$(message_during c3 8 solo_open)
check "one source at its limit" 503 "$(cat "$WORK/during-c3")"
check "the limit message" "$LIMIT_TEXT" "$message"
check "the file was not tried: it would count too" 0 "$(grep -c '/stream' "$WORK/gate-a.log")"

echo "--- one source at its limit, the other offline: the real cause is the offline one"
gate_mode stock down
mixed_open() { open_stream c4 "$tears" "$PROFILE_DIRECT"; }
message=$(message_during c4 8 mixed_open)
check "nothing can serve it" 503 "$(cat "$WORK/during-c4")"
check "the offline message" "$OFFLINE_TEXT" "$message"
gate_mode stock up

echo "--- one source at its limit, the other cannot transcode: the local fallback"
gate_mode stock notranscode
: >"$WORK/gate-a.log"
: >"$WORK/gate-stock.log"
check "still served" 200 "$(open_stream c5 "$tears" "$PROFILE_HLS")"
url=$(stream_url c5)
case "$url" in *JellyMesh/Session*) fallback=no ;; *) fallback=yes ;; esac
check "by the local server" yes "$fallback"
check "reading the file of the source with a free slot" true "$(grep -q 'GET /Videos/.*/stream 20' "$WORK/gate-stock.log" && echo true || echo false)"
check "and not the one at its limit" 0 "$(grep -c '/stream' "$WORK/gate-a.log")"
playlist=$(curl -sS "$url" | grep -v '^#' | head -1)
variant="${url%%master.m3u8*}$playlist"
segment=$(curl -sS "$variant" | grep -v '^#' | head -1)
check "the local transcode really plays" 200 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 60 "${url%%master.m3u8*}$segment")"
close_stream c5
gate_mode stock up
close_stream c1
check "free again" 0 "$(active_streams)"

echo "--- HLS keeps one slot through playlists and segments"
: >"$WORK/gate-a.log"
check "HLS stream" 200 "$(open_stream c6 "$tears" "$PROFILE_HLS")"
url=$(stream_url c6)
base=${url%%master.m3u8*}
master=$(curl -sS "$url")
variant=$(grep -v '^#' <<<"$master" | head -1)
segments=$(curl -sS "$base$variant" | grep -v '^#' | head -3)
for segment in $segments; do
  check "segment through b" 200 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 60 "$base$segment")"
done
check "one slot" 1 "$(active_streams)"
check "no request of the stream was refused" 0 "$(grep -c -E ' (403|404|429)$' "$WORK/gate-a.log")"
close_stream c6
check "free again" 0 "$(active_streams)"

echo "--- a new PlaybackInfo of the same client takes over its slot, as a change of audio track does"
: >"$WORK/gate-a.log"
check "open" 200 "$(open_stream c10 "$tears" "$PROFILE_DIRECT")"
check "reopen on a full server" 200 "$(open_stream c10 "$tears" "$PROFILE_DIRECT")"
check "a served both" 2 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-a.log")"
check "one slot" 1 "$(active_streams)"
close_stream c10

echo "--- a long stream holds its slot past the idle time"
check "open" 200 "$(open_stream c7 "$tears" "$PROFILE_DIRECT")"
url=$(stream_url c7)
curl -sS -o /dev/null --limit-rate 50k "$url" &
reader=$!
sleep 130
check "still held after 130 s, with one request running" 1 "$(active_streams)"
kill "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
close_stream c7
check "free again" 0 "$(active_streams)"

echo "--- a consumer that cannot release: the slot expires"
check "open" 200 "$(open_stream c8 "$tears" "$PROFILE_DIRECT")"
gate_mode a down
close_stream c8
gate_mode a up
check "the release did not reach a" 1 "$(active_streams)"
sleep 125
check "gone after the idle time" 0 "$(active_streams)"

echo "--- a consumer that vanishes in the middle of a stream: the slot expires"
check "open" 200 "$(open_stream c9 "$tears" "$PROFILE_DIRECT")"
url=$(stream_url c9)
curl -sS -o /dev/null --limit-rate 50k "$url" &
reader=$!
sleep 3
docker pause jm-prov-b >/dev/null
freed=never
for waited in $(seq 10 10 300); do
  sleep 10
  if [ "$(active_streams)" = 0 ]; then
    freed=$waited
    break
  fi
done
docker unpause jm-prov-b >/dev/null
kill "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
echo "slot of the frozen consumer freed after: $freed s"
check "the slot of a frozen consumer is freed" true "$([ "$freed" != never ] && echo true || echo false)"
close_stream c9

exit "$FAILED"
