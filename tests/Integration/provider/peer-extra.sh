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

# A real attack. mario plays on a like any local user, with a video encode that takes long enough to catch it running.
# Stock stops a play session for anyone who names it, whatever the device.
PROFILE_ENCODE=$(jq -c '. + {AllowVideoStreamCopy: false}' <<<"$PROFILE_HLS")
ffmpeg_pids() {
  docker exec jm-prov-$1 sh -c 'for p in /proc/[0-9]*; do case "$(readlink "$p/exe" 2>/dev/null)" in *ffmpeg*) echo "${p#/proc/}" ;; esac; done' | sort | tr '\n' ' '
}
transcode_files() { docker exec jm-prov-a sh -c 'ls /cache/transcodes | wc -l' | tr -d ' '; }
# login_as <a|b> <user> <device>
login_as() {
  curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"$3\", DeviceId=\"$3\", Version=\"0.1\"" \
    -H 'Content-Type: application/json' -d "{\"Username\":\"$2\",\"Pw\":\"jellymesh-test\"}" \
    "http://127.0.0.1:$(port_of "$1")/Users/AuthenticateByName" | jq -r .AccessToken
}
victim=$(login_as a mario victim-device)
victim_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"victim-device\", DeviceId=\"victim-device\", Version=\"0.1\", Token=\"$victim\""
# victim_plays: opens a stream on a and reads its first segment. Sets play_session, variant and the list of segments.
victim_plays() {
  curl -sS -o "$WORK/victim-open" -X POST -H "$ACCEPT" -H "Authorization: $victim_auth" -H 'Content-Type: application/json' \
    -d "$PROFILE_ENCODE" "http://127.0.0.1:18410/Items/$real/PlaybackInfo"
  play_session=$(jq -r .PlaySessionId "$WORK/victim-open")
  local url="http://127.0.0.1:18410$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/victim-open")"
  curl -sS -H "Authorization: $victim_auth" "$url" >"$WORK/victim-master"
  variant=$(folder_of "$url")$(grep -v '^#' "$WORK/victim-master" | head -1)
  curl -sS -H "Authorization: $victim_auth" "$variant" >"$WORK/victim-variant"
  victim_segment 1 >/dev/null
}
victim_segment() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 120 -H "Authorization: $victim_auth" "$(folder_of "$variant")$(grep -v '^#' "$WORK/victim-variant" | sed -n "$1p")"
}
victim_stops() {
  curl -sS -o /dev/null -X DELETE -H "Authorization: $victim_auth" "http://127.0.0.1:18410/Videos/ActiveEncodings?deviceId=victim-device&playSessionId=$play_session"
}
stop_as() { as_peer a "$1" DELETE "/Videos/ActiveEncodings?deviceId=$2&playSessionId=$3"; }

echo "--- a peer that knows the device and the play session of a local user cannot stop that transcode"
victim_plays
before=$(ffmpeg_pids a)
files=$(transcode_files)
echo "ffmpeg of the victim: $before, files: $files"
check "the transcode of the victim is running" true "$([ -n "$before" ] && echo true || echo false)"
check "the stop of a peer is refused" 403 "$(stop_as "$token" victim-device "$play_session")"
# A problem document with an identifier of the request, which is all that differs between two refusals.
refusal_body() { jq -c 'del(.traceId)' "$WORK/body"; }
refused_body=$(refusal_body)
check "and of a peer that calls the session by another name" 403 "$(stop_as "$token" victim-device "$(tr '[:lower:]' '[:upper:]' <<<"$play_session")")"
check "with the device alone" 403 "$(as_peer a "$token" DELETE "/Videos/ActiveEncodings?deviceId=victim-device")"
check "with an empty session" 403 "$(stop_as "$token" victim-device "")"
check "with a blank session" 403 "$(stop_as "$token" victim-device "%20")"
check "with two sessions" 403 "$(as_peer a "$token" DELETE "/Videos/ActiveEncodings?deviceId=victim-device&playSessionId=$play_session&playSessionId=x")"
check "an unknown session gets the same answer" 403 "$(stop_as "$token" victim-device "$(printf 'a%.0s' {1..32})")"
check "with the same body, so nothing tells them apart" "$refused_body" "$(refusal_body)"
segment_url="/Videos/$real/hls1/main/40.ts?MediaSourceId=$real&PlaySessionId=$play_session&DeviceId=attacker&VideoCodec=h264&AudioCodec=aac&runtimeTicks=1200000000&actualSegmentLengthTicks=30000000&SegmentContainer=ts"
check "a segment of a peer cannot name it: that stops the job too" 403 "$(as_peer a "$token" GET "$segment_url")"
check "a playlist of a peer cannot name it" 403 "$(as_peer a "$token" GET "/Videos/$real/master.m3u8?MediaSourceId=$real&PlaySessionId=$play_session")"
check "and none of this took a slot" 0 "$(active_streams)"
check "the victim keeps its ffmpeg" "$before" "$(ffmpeg_pids a)"
check "and its files" true "$([ "$(transcode_files)" -ge "$files" ] && echo true || echo false)"
check "the stream of the victim goes on" 200 "$(victim_segment 2)"
victim_stops

echo "--- nor a transcode of another peer"
post a /Users/New '{"Name":"luigi","Password":"jellymesh-test"}' >/dev/null
second=$(user_id a luigi)
req a PUT "/JellyMesh/Admin/Peers/$second" "$(jq -n --arg l "$library" '{SharedLibraryIds: [$l], MaxStreams: 1}')" >/dev/null
check "a second peer is connected" 204 "$(status)"
other=$(login_as a luigi other-peer)
open_as() { curl -sS -o "$WORK/opened" -w '%{http_code}' -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -d "$PROFILE_ENCODE" \
  -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$1\"" "http://127.0.0.1:18410/Items/$real/PlaybackInfo"; }
check "it opens a stream" 200 "$(open_as "$other")"
theirs=$(jq -r .PlaySessionId "$WORK/opened")
theirs_url=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/opened")
check "it plays" 200 "$(as_peer a "$other" GET "$theirs_url")"
check "the first peer cannot stop it" 403 "$(stop_as "$token" whatever "$theirs")"
check "its owner can" 204 "$(stop_as "$other" whatever "$theirs")"
as_peer a "$other" DELETE "/JellyMesh/Provider/Streams/$theirs" >/dev/null
req a DELETE "/JellyMesh/Admin/Peers/$second" >/dev/null

echo "--- what a peer opened stays its own after the slot is gone, so that its transcode can be closed"
check "open" 200 "$(open_as "$token")"
mine=$(jq -r .PlaySessionId "$WORK/opened")
mine_url=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/opened")
check "it plays" 200 "$(as_peer a "$token" GET "$mine_url")"
as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$mine" >/dev/null
check "the slot is gone" 0 "$(active_streams)"
check "its stop still works" 204 "$(stop_as "$token" whatever "$mine")"

echo "--- a peer opening sessions in a loop only forgets its own"
for i in $(seq 1 260); do
  open_as "$token" >/dev/null
  as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/opened")" >/dev/null
done
check "the oldest one is gone" 403 "$(stop_as "$token" whatever "$mine")"
check "the last one is not" 204 "$(stop_as "$token" whatever "$(jq -r .PlaySessionId "$WORK/opened")")"

echo "--- a local user of b cannot close a playback of another user of b"
mario=$(login_as b mario other-user)
mario_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"other-user\", DeviceId=\"other-user\", Version=\"0.1\", Token=\"$mario\""
report_as_mario() { curl -sS -o /dev/null -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $mario_auth" -H 'Content-Type: application/json' -d "{\"PlaySessionId\":\"$1\"}" http://127.0.0.1:18411/Sessions/Playing/Stopped; }
stop_as_mario() { curl -sS -o /dev/null -w '%{http_code}' -X DELETE -H "$ACCEPT" -H "Authorization: $mario_auth" "http://127.0.0.1:18411/Videos/ActiveEncodings?deviceId=$2&playSessionId=$1"; }
tears=$(movie_on_b "Tears of Steel")
check "open" 200 "$(open_stream x1 "$tears" "$PROFILE_HLS")"
session=$(jq -r .PlaySessionId "$WORK/open-x1")
url=$(stream_url x1)
check "it plays" 200 "$(curl -sS -o /dev/null -w '%{http_code}' "$url")"
check "a stop by another user is answered as stock would" 204 "$(stop_as_mario "$session" x1)"
check "a report by another user is answered as stock would" 204 "$(report_as_mario "$session")"
sleep 2
check "and the session is still there" 200 "$(curl -sS -o /dev/null -w '%{http_code}' "$url")"
close_stream x1
check "its own user closes it" 404 "$(curl -sS -o /dev/null -w '%{http_code}' "$url")"

echo "--- nor a local transcode of a stub"
gate_mode a notranscode
gate_mode stock notranscode
check "open" 200 "$(open_stream x2 "$tears" "$PROFILE_ENCODE")"
session=$(jq -r .PlaySessionId "$WORK/open-x2")
check "it plays" 200 "$(hls_segments x2 1)"
gate_mode a up
gate_mode stock up
# ffmpeg may finish a short clip by itself, so the files tell: a stop removes them, running or not.
local_files() { docker exec jm-prov-b sh -c 'ls /cache/transcodes | wc -l' | tr -d ' '; }
files=$(local_files)
check "the local transcode has its files" true "$([ "$files" -gt 0 ] && echo true || echo false)"
check "a stop by another user is answered as stock would" 204 "$(stop_as_mario "$session" x2)"
check "a report by another user is answered as stock would" 204 "$(report_as_mario "$session")"
foreign_segment="/Videos/$tears/hls1/main/40.ts?MediaSourceId=$tears&PlaySessionId=$session&DeviceId=other-user&VideoCodec=h264&AudioCodec=aac&runtimeTicks=1200000000&actualSegmentLengthTicks=30000000&SegmentContainer=ts"
check "a segment of another user cannot name it" 404 "$(curl -sS -o /dev/null -w '%{http_code}' --max-time 30 -H "Authorization: $mario_auth" "http://127.0.0.1:18411$foreign_segment")"
sleep 3
check "and the files of the local transcode stay" true "$([ "$(local_files)" -ge "$files" ] && echo true || echo false)"
close_stream x2
for i in $(seq 1 15); do
  [ "$(local_files)" = 0 ] && break
  sleep 1
done
check "its own user stops it" 0 "$(local_files)"

echo "--- a remote removed with a playback open gets its slot back at once, not at the end of the idle time"
from_b=$(cat "$WORK/id-a-from-b")
streams_on_b() { get b /JellyMesh/Admin/Peers | jq -r '.[0].ActiveStreams'; }
stub_on_a=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | startswith("/federata/"))][0].Id')
post a "/Items/$stub_on_a/PlaybackInfo" "$PROFILE_DIRECT" >/dev/null
check "a plays from b" 200 "$(status)"
check "b holds a slot" 1 "$(streams_on_b)"
req a DELETE "/JellyMesh/Admin/Servers/$from_b" >/dev/null
check "the remote is removed" 204 "$(status)"
check "b holds nothing" 0 "$(streams_on_b)"

exit "$FAILED"
