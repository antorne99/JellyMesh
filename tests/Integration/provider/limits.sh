#!/usr/bin/env bash
# The stream limit: b plays from a, with a limit of 1 for it. Over the limit b passes to the next source, to the local
# fallback, or to the 503, and a slot never leaks. Runs after federate.sh, and takes about 16 minutes because it waits for
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

echo "--- one source at its limit, the other offline: a server answered, so not the offline text (decision 17)"
gate_mode stock down
mixed_open() { open_stream c4 "$tears" "$PROFILE_DIRECT"; }
message=$(message_during c4 8 mixed_open)
check "nothing can serve it" 503 "$(cat "$WORK/during-c4")"
check "the message of a server that answered" "$FAILED_TEXT" "$message"
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

# wait_free: until nothing is held on a. Prints the seconds it took.
wait_free() {
  local waited
  for waited in $(seq 0 "${1:-130}"); do
    [ "$(active_streams)" = 0 ] && { echo "$waited"; return 0; }
    sleep 1
  done
  echo never
}
peer_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\""
# take_slot: another stream of the same peer asks. Prints the status; what it opened is in $WORK/taken.
take_slot() {
  curl -sS -o "$WORK/taken" -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" "http://127.0.0.1:18410/Items/$real/PlaybackInfo"
}
give_slot() {
  as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/taken")" >/dev/null
}
# slow_read <device> <range>: reads a stream of b slowly, in the background. The pid goes to $WORK/reader, the bytes to a file.
slow_read() {
  curl -sS -o "$WORK/read-$1" --limit-rate 100k ${2:+-r "$2"} "$(stream_url "$1")" &
  echo $! >"$WORK/reader"
}
kill_reader() {
  kill -9 "$(cat "$WORK/reader")" 2>/dev/null
  wait "$(cat "$WORK/reader")" 2>/dev/null
}

echo "wait for what the earlier cases left: $(wait_free) s"

echo "--- a probe of b drops its connection on purpose: a stream that has not started keeps its slot"
check "open HLS" 200 "$(open_stream c13 "$tears" "$PROFILE_HLS")"
sleep 7
check "held past the grace of a lost connection" 1 "$(active_streams)"
close_stream c13
check "free again" 0 "$(active_streams)"

echo "--- a client that drops its connection: the slot goes in seconds, with no help from b"
gate_mode a throttle:200
: >"$WORK/gate-a.log"
check "open" 200 "$(open_stream c14 "$tears" "$PROFILE_DIRECT")"
slow_read c14
sleep 3
check "it holds the slot" 1 "$(active_streams)"
kill_reader
freed=$(wait_free 30)
echo "slot of the dropped connection freed after: $freed s"
check "freed within seconds" true "$([ "$freed" != never ] && [ "$freed" -le 8 ] && echo true || echo false)"
check "b did not have to give it back" 0 "$(grep -c 'DELETE /JellyMesh/Provider/Streams' "$WORK/gate-a.log")"
check "another stream gets it" 200 "$(take_slot)"
give_slot
close_stream c14

echo "--- a seek at limit 1: the connection drops, the next one comes at once, and no other stream gets in between"
check "open" 200 "$(open_stream c15 "$tears" "$PROFILE_DIRECT")"
slow_read c15
sleep 3
kill_reader
slow_read c15 100000-
: >"$WORK/seek-others"
for i in $(seq 1 20); do
  take_slot >>"$WORK/seek-others"
  echo >>"$WORK/seek-others"
  sleep 0.5
done
check "no other stream got in, past the grace" 0 "$(grep -c 200 "$WORK/seek-others")"
check "they were told of the limit" 20 "$(grep -c 429 "$WORK/seek-others")"
check "the slot stayed with the stream" 1 "$(active_streams)"
check "the stream kept playing" true "$(kill -0 "$(cat "$WORK/reader")" 2>/dev/null && [ "$(wc -c <"$WORK/read-c15")" -gt 100000 ] && echo true || echo false)"
kill_reader
close_stream c15
check "free again" 0 "$(wait_free 15)"
gate_mode a up

echo "--- a consumer that gives up on a PlaybackInfo: a finishes it for nobody, and the slot does not stay"
# A local .strm is what makes the stock action slow: it runs ffprobe on its URL, here through the slowed gate. The peer calls
# a directly, because a gate that blocks on the server would not pass the disconnect on.
SLOW="JM Lento (2026)"
docker exec jm-prov-a sh -c "mkdir -p '/extra/film/$SLOW' && echo 'http://host.docker.internal:18412/Videos/$real/stream?static=true' > '/extra/film/$SLOW/$SLOW.strm'"
scan a
slow=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | contains("JM Lento"))][0].Id')
gate_mode a slow:6000
gave_up=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 2 -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" -H 'Content-Type: application/json' -d "$PROFILE_DIRECT" "http://127.0.0.1:18410/Items/$slow/PlaybackInfo")
check "the consumer timed out with no answer" 000 "$gave_up"
freed=$(wait_free 30)
gate_mode a up
echo "slot of the abandoned PlaybackInfo freed after: $freed s"
check "the slot went when the action ended" true "$([ "$freed" != never ] && [ "$freed" -le 8 ] && echo true || echo false)"
check "the next stream gets it" 200 "$(take_slot)"
give_slot
docker exec jm-prov-a rm -rf "/extra/film/$SLOW"
scan a

echo "--- a stream that fails on a: its slot goes, and a retry in time keeps it"
check "open" 200 "$(open_stream c17 "$solo" "$PROFILE_DIRECT")"
solo_file="/extra/film/$SOLO (2026)/$SOLO (2026).mkv"
docker exec jm-prov-a mv "$solo_file" "$solo_file.away"
url=$(stream_url c17)
status=$(curl -sS -o /dev/null -w '%{http_code}' "$url")
echo "what the client of the broken stream got: $status"
check "the failure reached the client" true "$([ "$status" -ge 400 ] && echo true || echo false)"
sleep 2
check "held for a retry" 1 "$(active_streams)"
curl -sS -o /dev/null "$url"
sleep 2
check "the retry kept it" 1 "$(active_streams)"
freed=$(wait_free 30)
echo "slot of the failed stream freed after: $freed s"
check "freed within seconds" true "$([ "$freed" != never ] && [ "$freed" -le 8 ] && echo true || echo false)"
docker exec jm-prov-a mv "$solo_file.away" "$solo_file"
close_stream c17

echo "--- a connected server changed or removed by the administrator: its slots go at once"
gate_mode a throttle:200
peer_user=$(user_id a peer)
check "open" 200 "$(open_stream c18 "$tears" "$PROFILE_DIRECT")"
slow_read c18
sleep 3
check "it holds the slot" 1 "$(active_streams)"
share a 2 Film Serie
check "a new limit frees it" 0 "$(active_streams)"
share a 1 Film Serie
check "saving the same grant holds nothing either" 0 "$(active_streams)"
kill_reader
close_stream c18
check "open" 200 "$(open_stream c19 "$tears" "$PROFILE_DIRECT")"
slow_read c19
sleep 3
check "it holds the slot" 1 "$(active_streams)"
req a DELETE "/JellyMesh/Admin/Peers/$peer_user" >/dev/null
check "removed" 204 "$(status)"
share a 1 Film Serie
check "connected again, nothing is held" 0 "$(active_streams)"
check "the slot is free at once" 200 "$(take_slot)"
give_slot
kill_reader
close_stream c19
gate_mode a up

echo "--- the slot goes between the probe of the local transcode and the read of ffmpeg: the refusal of the limit"
gate_mode a notranscode
check "served" 200 "$(open_stream c20 "$solo" "$PROFILE_HLS")"
url=$(stream_url c20)
case "$url" in *JellyMesh/Session*) fallback=no ;; *) fallback=yes ;; esac
check "by the local server" yes "$fallback"
gate_mode a up
check "another stream takes the slot first" 200 "$(take_slot)"
curl -sS "$url" >"$WORK/race-master"
variant=$(folder_of "$url")$(grep -v '^#' "$WORK/race-master" | head -1)
curl -sS "$variant" >"$WORK/race-variant"
segment=$(folder_of "$variant")$(grep -v '^#' "$WORK/race-variant" | head -1)
read_segment() { curl -sS -o /dev/null -w '%{http_code}' --max-time 60 "$segment"; }
message=$(message_during c20 8 read_segment)
check "the first segment is refused with a 503" 503 "$(cat "$WORK/during-c20")"
check "and the message of the limit" "$LIMIT_TEXT" "$message"
give_slot
check "with the slot free again the same URL plays" 200 "$(read_segment)"
close_stream c20
check "free again" 0 "$(wait_free 15)"

# The cases that need a stream long enough to catch running. Encoding takes minutes on it, and no buffer on the way holds it.
LONG="JM Lungo (2026)"
OTHER="JM Altro (2026)"
docker exec jm-prov-a sh -c "mkdir -p '/extra/film/$LONG' '/extra/altra/$OTHER' \
  && /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -loglevel error -nostdin -stream_loop 40 \
    -i \"\$(find '/media/film/Tears of Steel (2012)' -type f -name '*.m*' | head -1)\" -map 0 -c copy '/extra/film/$LONG/$LONG.mkv' \
  && ln '/extra/film/$LONG/$LONG.mkv' '/extra/altra/$OTHER/$OTHER.mkv'"
post a "/Library/VirtualFolders?name=Altra&collectionType=movies&refreshLibrary=false&paths=/extra/altra" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
scan a
sync_now b
scan b
long=$(movie_on_b "JM Lungo")
other=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | contains("JM Altro"))][0].Id')
echo "long film on b: $long, in the other library of a: $other"
# Segments of a minute: the buffers on the way hold a short one, and a would have nothing in flight when the client drops.
PROFILE_ENCODE=$(jq -c '. + {AllowVideoStreamCopy: false} | .DeviceProfile.TranscodingProfiles[0].SegmentLength = 60' <<<"$PROFILE_HLS")

# ended <pid> <seconds>: whether it stopped within that time.
ended() {
  local i
  for i in $(seq 1 $(( $2 * 2 ))); do
    kill -0 "$1" 2>/dev/null || { echo true; return 0; }
    sleep 0.5
  done
  echo false
}
# grows <file>: whether bytes keep coming in.
grows() {
  local before
  before=$(wc -c <"$1")
  sleep 3
  [ "$(wc -c <"$1")" -gt "$before" ] && echo true || echo false
}
# peer_read <item>: a peer playing an item by itself, slowly. The pid goes to $WORK/peer-reader.
peer_read() {
  curl -sS -o "$WORK/taken" -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" "http://127.0.0.1:18410/Items/$1/PlaybackInfo"
  curl -sS -o "$WORK/read-peer" --limit-rate 100k -H "Authorization: $peer_auth" \
    "http://127.0.0.1:18410/Videos/$1/stream?static=true&MediaSourceId=$1&PlaySessionId=$(jq -r .PlaySessionId "$WORK/taken")" &
  echo $! >"$WORK/peer-reader"
}
ffmpeg_pids() {
  docker exec jm-prov-a sh -c 'for p in /proc/[0-9]*; do case "$(readlink "$p/exe" 2>/dev/null)" in *ffmpeg*) echo "${p#/proc/}" ;; esac; done' | sort | tr '\n' ' ' | sed 's/ $//'
}
ffmpeg_running() { case " $(ffmpeg_pids) " in *" $1 "*) echo true ;; *) echo false ;; esac; }
ffmpeg_count() { docker exec jm-prov-a sh -c 'n=0; for p in /proc/[0-9]*; do case "$(readlink "$p/exe" 2>/dev/null)" in *ffmpeg*) n=$((n+1)) ;; esac; done; echo $n'; }
# no_ffmpeg <seconds>: seconds until a has none left, or never.
no_ffmpeg() {
  local i
  for i in $(seq 0 "$1"); do
    [ -z "$(ffmpeg_pids)" ] && { echo "$i"; return 0; }
    sleep 1
  done
  echo never
}
# first_segment <device>: the URL of the first segment of an HLS stream of b.
first_segment() {
  local url variant
  url=$(stream_url "$1")
  curl -sS "$url" >"$WORK/seen-$1-master"
  variant=$(folder_of "$url")$(grep -v '^#' "$WORK/seen-$1-master" | head -1)
  curl -sS "$variant" >"$WORK/seen-$1-variant"
  echo "$(folder_of "$variant")$(grep -v '^#' "$WORK/seen-$1-variant" | head -1)"
}
# slow_segment <url>: reads a segment slowly, in the background, so that the request is running. The pid goes to $WORK/reader.
slow_segment() {
  curl -sS -o /dev/null --limit-rate 20k "$1" &
  echo $! >"$WORK/reader"
}

echo "--- removing a connected server cuts what it is playing, at once"
check "open" 200 "$(open_stream c21 "$long" "$PROFILE_DIRECT")"
slow_read c21
reader=$(cat "$WORK/reader")
sleep 3
check "a long direct play is running" true "$(grows "$WORK/read-c21")"
req a DELETE "/JellyMesh/Admin/Peers/$peer_user" >/dev/null
check "removed" 204 "$(status)"
check "it was cut" true "$(ended "$reader" 3)"
kill -9 "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
share a 1 Film Serie
close_stream c21

echo "--- taking a library away cuts only what plays from it"
share a 2 Film Altra
check "open from the library that goes" 200 "$(open_stream c22 "$long" "$PROFILE_DIRECT")"
slow_read c22
reader=$(cat "$WORK/reader")
peer_read "$other"
kept=$(cat "$WORK/peer-reader")
sleep 3
check "both are playing" true "$(grows "$WORK/read-c22")"
check "both are playing" true "$(grows "$WORK/read-peer")"
share a 2 Altra
check "the stream of the library taken away is cut" true "$(ended "$reader" 3)"
kill -9 "$reader" 2>/dev/null
wait "$reader" 2>/dev/null
close_stream c22
check "the stream of the library still shared keeps playing" true "$(grows "$WORK/read-peer")"

echo "--- any other change to what a server may do cuts nothing"
share a 3 Altra
check "a higher limit" true "$(grows "$WORK/read-peer")"
share a 3 Altra
check "saving the same again" true "$(grows "$WORK/read-peer")"
share a 3 Altra Film
check "an added library" true "$(grows "$WORK/read-peer")"
kill -9 "$kept" 2>/dev/null
wait "$kept" 2>/dev/null
as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/taken")" >/dev/null
share a 1 Film Serie
check "free again" 0 "$(wait_free 15)"

echo "--- a stream that drops its connection: its ffmpeg goes when the grace ends, and no two overlap at limit 1"
gate_mode a throttle:100
check "open" 200 "$(open_stream c23 "$long" "$PROFILE_ENCODE")"
segment=$(first_segment c23)
slow_segment "$segment"
sleep 2
first_ffmpeg=$(ffmpeg_pids)
check "its ffmpeg is running" true "$([ -n "$first_ffmpeg" ] && echo true || echo false)"
(
  while [ ! -e "$WORK/stop-sampling" ]; do
    ffmpeg_count >>"$WORK/ffmpeg-samples"
    sleep 0.3
  done
) &
sampler=$!
rm -f "$WORK/stop-sampling" "$WORK/ffmpeg-samples"
kill_reader
opened=refused
for i in $(seq 1 15); do
  [ "$(open_stream c24 "$long" "$PROFILE_ENCODE")" = 200 ] && { opened=$i; break; }
  sleep 1
done
echo "the next stream got in after $opened attempts"
check "the next stream gets in" true "$([ "$opened" != refused ] && echo true || echo false)"
second_ffmpeg=$(ffmpeg_pids)
sleep 3
touch "$WORK/stop-sampling"
wait "$sampler" 2>/dev/null
rm -f "$WORK/stop-sampling"
echo "ffmpeg of the first stream: $first_ffmpeg, of the second: $second_ffmpeg, samples: $(wc -l <"$WORK/ffmpeg-samples"), most at once: $(sort -n "$WORK/ffmpeg-samples" | tail -1)"
check "there were samples" true "$([ "$(wc -l <"$WORK/ffmpeg-samples")" -gt 5 ] && echo true || echo false)"
check "never two ffmpeg at once" 1 "$(sort -n "$WORK/ffmpeg-samples" | tail -1)"
check "the first ffmpeg is gone" false "$(ffmpeg_running "$first_ffmpeg")"

echo "--- a seek within the grace does not touch the transcode"
segment=$(first_segment c24)
slow_segment "$segment"
sleep 2
before=$(ffmpeg_pids)
kill_reader
slow_segment "$segment"
sleep 8
check "the same ffmpeg goes on past the grace" "$before" "$(ffmpeg_pids)"
check "and the slot stayed" 1 "$(active_streams)"

echo "--- with nobody asking, the ffmpeg goes all the same"
kill_reader
gone=$(no_ffmpeg 15)
echo "ffmpeg of the dropped stream gone after: $gone s"
check "gone with the grace" true "$([ "$gone" != never ] && [ "$gone" -le 8 ] && echo true || echo false)"
check "and the slot" 0 "$(wait_free 3)"

echo "--- the consumer that comes back: over the limit it is told so, with a slot free it plays from there"
gate_mode a up
: >"$WORK/gate-a.log"
check "another stream has the slot" 200 "$(take_slot)"
check "the client gets no stream" 502 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 "$segment")"
check "because a refused it for the limit" true "$(grep -q -E 'GET /Videos/.*/hls1/.* 429' "$WORK/gate-a.log" && echo true || echo false)"
give_slot
check "with the slot free the same URL plays" 200 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 60 "$segment")"
check "and holds it" 1 "$(active_streams)"
close_stream c24
close_stream c23
check "no ffmpeg left" true "$(g=$(no_ffmpeg 10); [ "$g" != never ] && echo true || echo false)"
check "free again" 0 "$(wait_free 15)"

echo "--- a failed start of a local transcode for any reason: a retry plays, it does not hang"
gate_mode a notranscode
check "served" 200 "$(open_stream c25 "$solo" "$PROFILE_HLS")"
url=$(stream_url c25)
case "$url" in *JellyMesh/Session*) fallback=no ;; *) fallback=yes ;; esac
check "by the local server" yes "$fallback"
gate_mode a up
curl -sS "$url" >"$WORK/retry-master"
variant=$(folder_of "$url")$(grep -v '^#' "$WORK/retry-master" | head -1)
curl -sS "$variant" >"$WORK/retry-variant"
segment=$(folder_of "$variant")$(grep -v '^#' "$WORK/retry-variant" | head -1)
gate_mode a down
check "ffmpeg cannot read the file" 500 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 "$segment")"
gate_mode a up
check "the retry plays" 200 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 "$segment")"
close_stream c25
check "free again" 0 "$(wait_free 15)"

echo "--- a failed start of a transcode for a peer: a retry plays, it does not hang"
solo_on_a=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$SOLO" '[.Items[] | select(.Path | contains($t))][0].Id')
peer_get() { curl -sS -o "$2" -w '%{http_code}' --max-time 30 -H "Authorization: $peer_auth" "http://127.0.0.1:18410$1"; }
curl -sS -o "$WORK/retry-open" -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" -H 'Content-Type: application/json' -d "$PROFILE_ENCODE" "http://127.0.0.1:18410/Items/$solo_on_a/PlaybackInfo"
url=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/retry-open")
check "the master playlist" 200 "$(peer_get "$url" "$WORK/retry-master")"
variant=$(folder_of "$url")$(grep -v '^#' "$WORK/retry-master" | head -1)
check "the variant" 200 "$(peer_get "$variant" "$WORK/retry-variant")"
segment=$(folder_of "$variant")$(grep -v '^#' "$WORK/retry-variant" | head -1)
docker exec jm-prov-a mv "$solo_file" "$solo_file.away"
status=$(peer_get "$segment" /dev/null)
docker exec jm-prov-a mv "$solo_file.away" "$solo_file"
echo "what the peer got from the failed start: $status"
check "the start failed" true "$([ "$status" -ge 500 ] && echo true || echo false)"
check "the retry plays" 200 "$(peer_get "$segment" /dev/null)"
as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/retry-open")" >/dev/null
stop=$(jq -r .PlaySessionId "$WORK/retry-open")
as_peer a "$token" DELETE "/Videos/ActiveEncodings?deviceId=x&playSessionId=$stop" >/dev/null

req a DELETE "/Library/VirtualFolders?name=Altra" >/dev/null
docker exec jm-prov-a rm -rf "/extra/film/$LONG" /extra/altra
scan a

exit "$FAILED"
