#!/usr/bin/env bash
# A user of the provider cannot stop the transcode of a peer with a segment that names no play session: stock would stop
# every job of the device the segment names. Nor can another peer, by naming that play session in the legacy params. One private server of its own, removed at the end unless KEEP=1.
# usage: MEDIA=<main checkout>/tests/Integration/media WORK=<scratch folder> peer-device.sh
set -uo pipefail
export PREFIX=${PREFIX:-jm-q92} PORT_A=${PORT_A:-8280}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0

cleanup() {
  [ "${KEEP:-0}" = 1 ] && return
  local name
  name=$(container_of a)
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
  echo "removed $name"
}
trap cleanup EXIT

start a remoto-standard
install_plugin a
cfg=$(get a "/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration" | jq '.FederatedFolderPath = "/federata"')
post a "/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration" "$cfg" >/dev/null
get a /Plugins | jq -c '.[] | select(.Name == "JellyMesh") | {Name, Version, Status}'
share a 2 Film

base="http://127.0.0.1:$(port_of a)"
real=$(get a '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '[.Items[] | select(.Path | startswith("/media/"))][0].Id')
# The device a consumer sends: fixed for the remote server, from a random identifier.
peer_device="jellymesh-$(openssl rand -hex 16)"
login_as() {
  curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"$3\", DeviceId=\"$3\", Version=\"0.1\"" \
    -H 'Content-Type: application/json' -d "{\"Username\":\"$2\",\"Pw\":\"jellymesh-test\"}" "$base/Users/AuthenticateByName" | jq -r .AccessToken
}
peer_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"JellyMesh\", DeviceId=\"$peer_device\", Version=\"0.1\", Token=\"$(login_as a peer "$peer_device")\""
mario_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"mario-device\", DeviceId=\"mario-device\", Version=\"0.1\", Token=\"$(login_as a mario mario-device)\""
PROFILE_ENCODE=$(jq -c '. + {AllowVideoStreamCopy: false}' <<<"$PROFILE_HLS")
ffmpeg_pids() {
  docker exec "$(container_of a)" sh -c 'for p in /proc/[0-9]*; do case "$(readlink "$p/exe" 2>/dev/null)" in *ffmpeg*) echo "${p#/proc/}" ;; esac; done' | sort | tr '\n' ' '
}

echo "--- a peer plays with a transcode, as a consumer does"
check "it opens a stream" 200 "$(curl -sS -o "$WORK/peer-open" -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" \
  -H 'Content-Type: application/json' -d "$PROFILE_ENCODE" "$base/Items/$real/PlaybackInfo")"
url="$base$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/peer-open")"
curl -sS -H "Authorization: $peer_auth" "$url" >"$WORK/peer-master"
variant=$(folder_of "$url")$(grep -v '^#' "$WORK/peer-master" | head -1)
curl -sS -H "Authorization: $peer_auth" "$variant" >"$WORK/peer-variant"
peer_segment() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 120 -H "Authorization: $peer_auth" "$(folder_of "$variant")$(grep -v '^#' "$WORK/peer-variant" | sed -n "$1p")"
}
check "its first segment" 200 "$(peer_segment 1)"
before=$(ffmpeg_pids a)
echo "ffmpeg of the peer: $before"
check "its transcode is running" true "$([ -n "$before" ] && echo true || echo false)"

echo "--- a segment with no play session that would stop it"
# segment <auth or empty> <query>: a segment far ahead, which makes stock restart ffmpeg.
segment() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 60 ${1:+-H "Authorization: $1"} \
    "$base/Videos/$real/hls1/main/40.ts?MediaSourceId=$real&VideoCodec=h264&AudioCodec=aac&runtimeTicks=1200000000&actualSegmentLengthTicks=30000000&SegmentContainer=ts$2"
}
check "of a local user on the device of the peer" 404 "$(segment "$mario_auth" "&DeviceId=$peer_device")"
check "on the device in capitals" 404 "$(segment "$mario_auth" "&DeviceId=$(tr '[:lower:]' '[:upper:]' <<<"$peer_device")")"
check "on the device in the legacy params" 404 "$(segment "$mario_auth" "&DeviceId=mario-device&params=;$peer_device")"
# Stock asks a segment for a user before any filter runs.
check "of nobody on the device of the peer" 401 "$(segment "" "&DeviceId=$peer_device")"
check "the peer keeps its ffmpeg" "$before" "$(ffmpeg_pids a)"
check "and its stream goes on" 200 "$(peer_segment 2)"

echo "--- a segment with no play session and no device, while the job of the peer has a device (Q95)"
# Stock stops only the jobs without a device for it, so it goes to stock and starts an ffmpeg of its own.
running=$(ffmpeg_pids a)
check "of a local user goes to stock" 200 "$(segment "$mario_auth" "")"
after=" $(ffmpeg_pids a) "
check "the peer keeps its ffmpeg" 0 "$(for p in $running; do [[ "$after" == *" $p "* ]] || echo "$p"; done | grep -c . || true)"
check "and its stream goes on" 200 "$(peer_segment 3)"
# The ffmpeg of that segment would end on its own in the middle of the checks below.
docker exec "$(container_of a)" sh -c "kill $(for p in $after; do [[ " $running " == *" $p "* ]] || echo "$p"; done)" 2>/dev/null

echo "--- another peer names the play session of the first in the legacy params, which stock reads first"
post a /Users/New '{"Name":"peer2","Password":"jellymesh-test"}' >/dev/null
req a PUT "/JellyMesh/Admin/Peers/$(user_id a peer2)" "$(jq -n --arg l "$(library_id a Film)" '{SharedLibraryIds: [$l], MaxStreams: 2}')" >/dev/null
check "the second peer is granted" 204 "$(status)"
peer2_device="jellymesh-$(openssl rand -hex 16)"
peer2_auth="MediaBrowser Client=\"JellyMeshProv\", Device=\"JellyMesh\", DeviceId=\"$peer2_device\", Version=\"0.1\", Token=\"$(login_as a peer2 "$peer2_device")\""
check "it opens a play session of its own" 200 "$(curl -sS -o "$WORK/peer2-open" -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $peer2_auth" \
  -H 'Content-Type: application/json' -d "$PROFILE_ENCODE" "$base/Items/$real/PlaybackInfo")"
own=$(jq -r .PlaySessionId "$WORK/peer2-open")
victim=$(jq -r .PlaySessionId "$WORK/peer-open")
# field20 <value>: a legacy params whose field 20, the play session, is the value.
field20() { printf '%s' "$(printf '%%3B%.0s' $(seq 1 20))$1"; }
alive() { docker exec "$(container_of a)" sh -c "for p in $1; do test -d /proc/\$p || exit 1; done" && echo true || echo false; }
pids=$(ffmpeg_pids a)
echo "ffmpeg of the first peer: $pids"
check "it still runs" true "$([ -n "$pids" ] && echo true || echo false)"
check "its own in the query, the first peer's in field 20" 403 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&PlaySessionId=$own&params=$(field20 "$victim")")"
check "the first peer's in the query, a blank field 20" 403 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&PlaySessionId=$victim&params=$(field20 '%20')%3Bx")"
hls="MediaSourceId=$real&VideoCodec=h264&AudioCodec=aac&runtimeTicks=1200000000&actualSegmentLengthTicks=30000000&SegmentContainer=ts"
# encoded <auth> <path> <query> [method]: the query sent as one encoded key, which Jellyfin decodes with keys that differ by case.
encoded() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 60 -X "${4:-GET}" -H "Authorization: $1" "$base$2?$(jq -rn --arg q "$3" '$q | @uri')"
}
check "the first peer's under the bound name of an encoded query" 403 "$(encoded "$peer2_auth" "/Videos/$real/hls1/main/40.ts" "$hls&DeviceId=$peer2_device&playSessionId=$victim&PlaySessionId=$own")"
check "the same on the close route" 403 "$(encoded "$peer2_auth" /Videos/ActiveEncodings "deviceId=$peer2_device&playSessionId=$victim&PlaySessionId=$own" DELETE)"
check "the first peer keeps its ffmpeg" true "$(alive "$pids")"
# Stock binds the first of a repeated params: here its own, so it leaves the first peer's job alone.
check "its own in the first params, the first peer's in the second" 200 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&PlaySessionId=$own&params=$(field20 "$own")&params=$(field20 "$victim")")"
check "the first peer still keeps its ffmpeg" true "$(alive "$pids")"
check "its own in the query, a bare first params, the first peer's in the second" 200 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&PlaySessionId=$own&params=x&params=$(field20 "$victim")")"
check "the first peer keeps its ffmpeg again" true "$(alive "$pids")"
check "its own in field 20 alone" 200 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&params=$(field20 "$own")")"
check "its own in the query, a blank field 20" 200 "$(segment "$peer2_auth" "&DeviceId=$peer2_device&PlaySessionId=$own&params=$(field20 '')%3Bx")"
check "the first peer keeps its ffmpeg to the end" true "$(alive "$pids")"
check "and its stream goes on" 200 "$(peer_segment 4)"
# The proof that stock acts on field 20: the first peer naming its own there with junk in the query restarts its job.
check "the first peer with its own in field 20 and junk in the query" 200 "$(segment "$peer_auth" "&DeviceId=$peer_device&PlaySessionId=junk&params=$(field20 "$victim")")"
check "restarts its own ffmpeg" false "$(alive "$pids")"

# --- Q103: local user on a peer play session (begin) ---
echo "--- a local user of the provider names the live play session of the peer (Q103)"
# Stock answers 204 to a stop and a report whatever it does, so only the ffmpeg of the peer tells them apart.
q103_before=" $(ffmpeg_pids a) "
check "the peer opens another play session" 200 "$(curl -sS -o "$WORK/q103-open" -w '%{http_code}' -X POST -H "$ACCEPT" -H "Authorization: $peer_auth" \
  -H 'Content-Type: application/json' -d "$PROFILE_ENCODE" "$base/Items/$real/PlaybackInfo")"
q103_session=$(jq -r .PlaySessionId "$WORK/q103-open")
q103_url="$base$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/q103-open")"
curl -sS -H "Authorization: $peer_auth" "$q103_url" >"$WORK/q103-master"
q103_variant=$(folder_of "$q103_url")$(grep -v '^#' "$WORK/q103-master" | head -1)
curl -sS -H "Authorization: $peer_auth" "$q103_variant" >"$WORK/q103-variant"
q103_segment() {
  curl -sS -o /dev/null -w '%{http_code}' --max-time 120 -H "Authorization: $peer_auth" "$(folder_of "$q103_variant")$(grep -v '^#' "$WORK/q103-variant" | sed -n "$1p")"
}
check "its first segment" 200 "$(q103_segment 1)"
q103_pids=$(for p in $(ffmpeg_pids a); do [[ "$q103_before" == *" $p "* ]] || echo "$p"; done | tr '\n' ' ')
echo "ffmpeg of that play session: $q103_pids"
check "its transcode is running" true "$([ -n "$q103_pids" ] && echo true || echo false)"
check "a local user closes its transcode" 204 "$(curl -sS -o /dev/null -w '%{http_code}' -X DELETE -H "Authorization: $mario_auth" \
  "$base/Videos/ActiveEncodings?deviceId=mario-device&playSessionId=$q103_session")"
check "the peer keeps its ffmpeg" true "$(alive "$q103_pids")"
check "a local user reports it stopped" 204 "$(curl -sS -o /dev/null -w '%{http_code}' -X POST -H "Authorization: $mario_auth" -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg s "$q103_session" --arg i "$real" '{ItemId: $i, PlaySessionId: $s, PositionTicks: 0}')" "$base/Sessions/Playing/Stopped")"
check "the peer still keeps its ffmpeg" true "$(alive "$q103_pids")"
check "a local user asks a segment far ahead in it" 404 "$(segment "$mario_auth" "&DeviceId=mario-device&PlaySessionId=$q103_session")"
check "the same in capitals" 404 "$(segment "$mario_auth" "&DeviceId=mario-device&PlaySessionId=$(tr '[:lower:]' '[:upper:]' <<<"$q103_session")")"
check "the same in field 20" 404 "$(segment "$mario_auth" "&DeviceId=mario-device&params=$(field20 "$q103_session")")"
check "the peer keeps its ffmpeg to the end" true "$(alive "$q103_pids")"
check "and its stream goes on" 200 "$(q103_segment 2)"
# --- Q103: local user on a peer play session (end) ---

echo "--- what a user of the provider sees of the peer"
curl -sS -H "$ACCEPT" -H "Authorization: $mario_auth" "$base/Sessions" >"$WORK/mario-sessions"
check "its device is not in the sessions a user reads" 0 "$(grep -c "$peer_device" "$WORK/mario-sessions")"
curl -sS -o "$WORK/mario-devices" -w '%{http_code}' -H "$ACCEPT" -H "Authorization: $mario_auth" "$base/Devices" >"$WORK/status"
check "nor in the devices, which a user cannot read" 0 "$(grep -c "$peer_device" "$WORK/mario-devices")"
echo "devices answered $(status) to a user"

echo "--- a segment with no play session on a device of nobody else still passes"
check "of a local user on its own device" 200 "$(segment "$mario_auth" "&DeviceId=mario-device")"

exit "$FAILED"
