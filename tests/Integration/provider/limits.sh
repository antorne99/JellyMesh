#!/usr/bin/env bash
# The stream limit: b plays from a, with a limit of 1 for it. Over the limit b passes to the next source, to the local
# fallback, or to the 503, and a slot never leaks. Runs after federate.sh, and takes about 8 minutes because it waits for
# real timeouts. usage: MEDIA=... WORK=... limits.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
SOLO="JM Solo Provider"
REFUSED=' (403|404|429)$'

# Never grep -q on a pipe here: with pipefail, grep leaving early makes a found line a failure.
limit_lines() { docker logs jm-prov-b 2>&1 | grep -c 'is at its stream limit'; }
transcode_files() { docker exec jm-prov-a sh -c 'ls /cache/transcodes | wc -l' | tr -d ' '; }
# wait_transcodes <files>: a removes them about 2 s after the stop.
wait_transcodes() {
  local i
  for i in $(seq 1 15); do
    [ "$(transcode_files)" -le "$1" ] && break
    sleep 1
  done
  transcode_files
}

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
logged=$(limit_lines)
check "first stream" 200 "$(open_stream c1 "$tears" "$PROFILE_DIRECT")"
check "it holds the slot on a" 1 "$(active_streams)"
check "it came from a" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-a.log")"
check "second stream" 200 "$(open_stream c2 "$tears" "$PROFILE_DIRECT")"
check "a refused it" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 429' "$WORK/gate-a.log")"
check "the stock remote served it" 1 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-stock.log")"
check "a still holds one slot" 1 "$(active_streams)"
sleep 1
check "b logged the limit" true "$([ "$(limit_lines)" -gt "$logged" ] && echo true || echo false)"
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
check "the local transcode really plays" 200 "$(hls_segments c5 1)"
close_stream c5
gate_mode stock up
close_stream c1
check "free again" 0 "$(active_streams)"

echo "--- HLS keeps one slot through playlists and segments"
: >"$WORK/gate-a.log"
files=$(transcode_files)
check "HLS stream" 200 "$(open_stream c6 "$tears" "$PROFILE_HLS")"
check "three segments through b" "200 200 200" "$(echo $(hls_segments c6 3))"
check "one slot" 1 "$(active_streams)"
check "no request of the stream was refused" 0 "$(grep -c -E "$REFUSED" "$WORK/gate-a.log")"
close_stream c6
check "free again" 0 "$(active_streams)"
check "the transcode was closed on a" 1 "$(grep -c 'DELETE /Videos/ActiveEncodings 204' "$WORK/gate-a.log")"
check "no request of the close was refused" 0 "$(grep -c -E "$REFUSED" "$WORK/gate-a.log")"
check "closing needs no new login" 0 "$(grep -c 'AuthenticateByName' "$WORK/gate-a.log")"
check "a keeps no file of the transcode" "$files" "$(wait_transcodes "$files")"

echo "--- a new PlaybackInfo of the same client takes over its slot, as a change of audio track does"
: >"$WORK/gate-a.log"
check "open" 200 "$(open_stream c10 "$tears" "$PROFILE_DIRECT")"
check "reopen on a full server" 200 "$(open_stream c10 "$tears" "$PROFILE_DIRECT")"
check "a served both" 2 "$(grep -c 'POST /Items/.*/PlaybackInfo 200' "$WORK/gate-a.log")"
check "one slot" 1 "$(active_streams)"
close_stream c10

echo "--- a client still reading holds its slot, long after a has sent everything"
: >"$WORK/gate-a.log"
check "open" 200 "$(open_stream c7 "$tears" "$PROFILE_DIRECT")"
url=$(stream_url c7)
curl -sS -o /dev/null --limit-rate 50k "$url" &
reader=$!
sleep 130
# The buffers on the way hold the file, so a is done early and has nothing in flight: only the renewals can hold the slot.
check "a has sent the whole file" 1 "$(grep -c -E 'END /Videos/.*/stream [0-9]{3,}$' "$WORK/gate-a.log")"
check "the client is still reading" true "$(kill -0 "$reader" 2>/dev/null && echo true || echo false)"
check "still held after 130 s" 1 "$(active_streams)"
check "b renewed it while the client read" true "$([ "$(grep -c 'PUT /JellyMesh/Provider/Streams/.* 204' "$WORK/gate-a.log")" -ge 3 ] && echo true || echo false)"
check "so the limit still counts it" 503 "$(open_stream c12 "$solo" "$PROFILE_DIRECT")"

echo "--- a client that leaves without a word stops the renewals: b frees the slot"
kill "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
: >"$WORK/gate-a.log"
freed=never
for waited in $(seq 10 10 120); do
  sleep 10
  if [ "$(active_streams)" = 0 ]; then
    freed=$waited
    break
  fi
done
echo "slot of the client that left freed after: $freed s"
check "free again" true "$([ "$freed" != never ] && echo true || echo false)"
check "b gave it back, a did not have to expire it" 1 "$(grep -c 'DELETE /JellyMesh/Provider/Streams/.* 204' "$WORK/gate-a.log")"
close_stream c7

echo "--- a pause: nothing keeps the stream alive on a, and resuming works (decision 6)"
files=$(transcode_files)
check "open" 200 "$(open_stream c11 "$tears" "$PROFILE_HLS")"
check "two segments before the pause" "200 200" "$(echo $(hls_segments c11 2))"
: >"$WORK/gate-a.log"
sleep 112
check "no renewal during the pause" 0 "$(grep -c 'PUT /JellyMesh/Provider/Streams' "$WORK/gate-a.log")"
check "no ping during the pause" 0 "$(grep -c '/Sessions/Playing' "$WORK/gate-a.log")"
check "b closed the transcode" 1 "$(grep -c 'DELETE /Videos/ActiveEncodings 204' "$WORK/gate-a.log")"
check "and gave the slot back" 0 "$(active_streams)"
check "a keeps no file of it" "$files" "$(wait_transcodes "$files")"
check "resuming with the same URLs works" 200 "$(hls_segments c11 1)"
check "and takes a slot again" 1 "$(active_streams)"
close_stream c11
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
