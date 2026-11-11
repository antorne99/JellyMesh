#!/usr/bin/env bash
# A user of the provider cannot stop the transcode of a peer with a segment that names no play session: stock would stop
# every job of the device the segment names. One private server of its own, removed at the end unless KEEP=1.
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
check "on no device at all" 404 "$(segment "$mario_auth" "")"
# Stock asks a segment for a user before any filter runs.
check "of nobody on the device of the peer" 401 "$(segment "" "&DeviceId=$peer_device")"
check "the peer keeps its ffmpeg" "$before" "$(ffmpeg_pids a)"
check "and its stream goes on" 200 "$(peer_segment 2)"

echo "--- what a user of the provider sees of the peer"
curl -sS -H "$ACCEPT" -H "Authorization: $mario_auth" "$base/Sessions" >"$WORK/mario-sessions"
check "its device is not in the sessions a user reads" 0 "$(grep -c "$peer_device" "$WORK/mario-sessions")"
curl -sS -o "$WORK/mario-devices" -w '%{http_code}' -H "$ACCEPT" -H "Authorization: $mario_auth" "$base/Devices" >"$WORK/status"
check "nor in the devices, which a user cannot read" 0 "$(grep -c "$peer_device" "$WORK/mario-devices")"
echo "devices answered $(status) to a user"

echo "--- a segment with no play session on a device of nobody else still passes"
check "of a local user on its own device" 200 "$(segment "$mario_auth" "&DeviceId=mario-device")"

exit "$FAILED"
