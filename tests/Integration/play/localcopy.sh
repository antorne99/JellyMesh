#!/usr/bin/env bash
# Decision 17, Antonio's choice: a stub opened while its remote is offline and a local copy has arrived plays the local copy.
# Real jellyfin-web in Chromium, as a user would. Run after fallback.sh. Can be run again.
set -euo pipefail
# The database helpers of versions/, with the names and ports of the play proofs: linked alternates leave the lists.
PREFIX=${PREFIX:-jm-play}
PORT=${PORT:-18430}
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"

FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
DIRECT="JM Confronto Bitrate"   # the browser plays this copy as it is: VP9 and Opus
TRANSCODE="JM Confronto Codec"  # and has to transcode this one: H.264 and AAC, which Chromium lacks
PRIVATE="JM Confronto Risoluzione"  # this copy is in a library the viewer cannot see
NOWHERE="Tears of Steel"        # no copy at all

docker start "$PREFIX-remote" >/dev/null
wait_ready remote
login local
viewer_login
uid=$(user_id local viewer)

# From the top: the copies gone, the stubs back.
docker exec "$PREFIX-local" sh -c 'rm -rf /media/film/JM* /media/privata/JM*'
scan local
sync_now local
scan local
check "the stubs are back" 1 "$([ -n "$(stub_item local "$DIRECT")" ] && [ -n "$(stub_item local "$PRIVATE")" ] && echo 1 || echo 0)"

nfo() { # nfo <title>: the ID of a title, so that the local copy has the identity of the stub
  docker exec "$PREFIX-remote" sh -c "cat \"/media/film/$1 (2026)/movie.nfo\""
}
copy() { # copy <library folder> <title> <extension> <ffmpeg codec arguments>
  local folder="/media/$1/$2 (2026)"
  docker exec "$PREFIX-local" mkdir -p "$folder"
  nfo "$2" | docker exec -i "$PREFIX-local" sh -c "cat > \"$folder/movie.nfo\""
  docker exec "$PREFIX-local" "$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i testsrc=d=30:s=640x360:r=25 -f lavfi -i sine=f=440:d=30 $4 "$folder/$2 (2026).$3"
}

copy film "$DIRECT" webm "-c:v libvpx-vp9 -b:v 400k -deadline realtime -cpu-used 8 -c:a libopus -b:a 64k"
copy film "$TRANSCODE" mp4 "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac"
copy privata "$PRIVATE" mp4 "-c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac"

# A refresh of one folder adds the copies and, unlike a full scan, runs no post-scan task: this is the window.
for name in Film Privato; do
  folder=$(get local /Library/VirtualFolders | jq -r --arg n "$name" '.[] | select(.Name == $n) | .ItemId')
  post local "/Items/$folder/Refresh?Recursive=true" >/dev/null
done
for i in $(seq 1 60); do
  [ -n "$(local_item local "$DIRECT")" ] && [ -n "$(local_item local "$TRANSCODE")" ] && [ -n "$(local_item local "$PRIVATE")" ] && break
  sleep 2
done
# The scan adds an item first and reads its .nfo a moment later: the identity is known from then on.
for title in "$DIRECT" "$TRANSCODE" "$PRIVATE"; do
  item=$(local_item local "$title")
  post local "/Items/$item/Refresh?metadataRefreshMode=Default" >/dev/null
  for i in $(seq 1 60); do
    [ -n "$(get local "/Items?Ids=$item&Fields=ProviderIds" | jq -r '.Items[0].ProviderIds.Tmdb // empty')" ] && break
    sleep 1
  done
done
stub_direct=$(stub_item local "$DIRECT")
stub_transcode=$(stub_item local "$TRANSCODE")
stub_private=$(stub_item local "$PRIVATE")
stub_nowhere=$(stub_item local "$NOWHERE")
local_direct=$(local_item local "$DIRECT")
local_transcode=$(local_item local "$TRANSCODE")
check "the stub and the copy are both in the library" 1 "$([ -n "$stub_direct" ] && [ -n "$local_direct" ] && echo 1 || echo 0)"

docker stop "$PREFIX-remote" >/dev/null

chosen=$(jq -n '{DeviceProfile: {MaxStreamingBitrate: 120000000, DirectPlayProfiles: [{Type: "Video", Container: "webm", VideoCodec: "vp9", AudioCodec: "opus"}], TranscodingProfiles: [{Type: "Video", Protocol: "hls", Container: "ts", VideoCodec: "h264", AudioCodec: "aac", Context: "Streaming"}]}}')
check "the API answers with the local file" 200 "$(as_viewer POST "/Items/$stub_direct/PlaybackInfo?userId=$uid" "$chosen")"
jq -c '.MediaSources[] | {Id, Path, TranscodingUrl: (.TranscodingUrl | sub("ApiKey=[^&]*"; "ApiKey=***")), TranscodingSubProtocol, Container}' "$WORK/viewer-body"
url=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/viewer-body")
check "that URL gives the file, with Range" 206 "$(curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "http://127.0.0.1:$PORT$url")"
check "a GET with no profile is served too" 200 "$(as_viewer GET "/Items/$stub_direct/PlaybackInfo?userId=$uid")"
jq -c '.MediaSources[] | {Id, Path, TranscodingUrl: (.TranscodingUrl | sub("ApiKey=[^&]*"; "ApiKey=***"))}' "$WORK/viewer-body"

echo "--- a track number of the stub, which the local file does not have"
tracks=$(echo "$chosen" | jq -c '.DeviceProfile.DirectPlayProfiles = [] | .AudioStreamIndex = 7 | .SubtitleStreamIndex = 8')
check "the transcode is offered" 200 "$(as_viewer POST "/Items/$stub_transcode/PlaybackInfo?userId=$uid" "$tracks")"
master=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/viewer-body")
echo "${master%%\?*}: $(echo "$master" | grep -o 'AudioStreamIndex=[0-9]*\|SubtitleStreamIndex=[0-9-]*' | tr '\n' ' ')"
check "the master playlist opens" 200 "$(curl -s -o "$WORK/master.m3u8" -w '%{http_code}' "http://127.0.0.1:$PORT$master")"
variant=$(grep -v '^#' "$WORK/master.m3u8" | head -1)
folder=${master%%master.m3u8*}
check "the variant playlist opens" 200 "$(curl -s -o "$WORK/variant.m3u8" -w '%{http_code}' "http://127.0.0.1:$PORT$folder$variant")"
segment=$(grep -v '^#' "$WORK/variant.m3u8" | head -1)
check "and ffmpeg gives a first segment" 200 "$(curl -s -o /dev/null -m 60 -w '%{http_code}' "http://127.0.0.1:$PORT$folder$segment")"
session=$(jq -r .PlaySessionId "$WORK/viewer-body")
as_viewer DELETE "/Videos/ActiveEncodings?deviceId=$PREFIX-viewer&playSessionId=$session" >/dev/null

time_of() { grep -a '^PROGRESS' "$WORK/web/$1.out" | sed -E "s/.*'t': ([0-9.]+).*/\1/"; }
src_of() { grep -a '^PROGRESS' "$WORK/web/$1.out" | sed -E "s/.*'src': '([^']*)'.*/\1/"; }
advanced() { awk -v t="${1:-0}" 'BEGIN { print (t > 1) ? 1 : 0 }'; }
play() { # play <item> <scenario> <capture> <seconds>
  web "$1" "$2" "$3" "$4" | tee "$WORK/web/$3.out" | grep -aE '^(STARTED|PROGRESS|SEEK|SEEN|STATE|DIALOGS|RES (4|5)|PAGEERR)' | cut -c1-230 || true
}
user_data() { get local "/Items/$1?userId=$uid" | jq -c '.UserData | {PlaybackPositionTicks, Played, LastPlayedDate}'; }

echo "--- the copy the browser plays as it is, seeking both ways"
play "$stub_direct" seek local-direct 14
check "the video really advances" 1 "$(advanced "$(time_of local-direct)")"
check "it reads the local item" 1 "$(src_of local-direct | grep -c "/Videos/$local_direct/stream.webm")"
data=$(user_data "$stub_direct")
echo "watch state on the stub: $data"
check "the state landed on the stub, the item the user opened (under 5 minutes a stopped video counts as played)" true "$(echo "$data" | jq -r .Played)"
echo "watch state on the copy, before the scan: $(user_data "$local_direct")"

echo "--- the copy the browser cannot play"
play "$stub_transcode" play local-transcode 14
check "the video really advances" 1 "$(advanced "$(time_of local-transcode)")"
check "it is a transcode of the local item" 1 "$(grep -a 'REQ GET' "$WORK/web/local-transcode.out" | grep -c -m1 "/videos/$local_transcode/\|/videos/$(echo "$local_transcode" | sed -E 's/^(.{8})(.{4})(.{4})(.{4})(.{12})$/\1-\2-\3-\4-\5/')/")"
sleep 3
check "the server closed that transcode" 0 "$(docker exec "$PREFIX-local" sh -c 'grep -l ffmpeg /proc/[0-9]*/comm 2>/dev/null | wc -l')"

echo "--- nothing anywhere"
play "$stub_nowhere" fail nowhere 12
check "Server non raggiungibile is shown" 1 "$(grep -a '^SEEN' "$WORK/web/nowhere.out" | grep -c 'Server non raggiungibile')"

echo "--- a copy the viewer cannot see"
check "the viewer does not get the copy" 503 "$(as_viewer POST "/Items/$stub_private/PlaybackInfo?userId=$uid" "$chosen")"
play "$stub_private" fail private 12
check "the viewer is told what anyone without a copy is told" 1 "$(grep -a '^SEEN' "$WORK/web/private.out" | grep -c 'Server non raggiungibile')"
admin=$(req local POST "/Items/$stub_private/PlaybackInfo" "$chosen" | jq -r '.MediaSources[0].TranscodingUrl' | grep -c master.m3u8 || true)
check "the admin, who sees it, gets the copy" 1 "$admin"

echo "--- the next full scan: a local copy no longer removes the stub, nor takes its state (Q18 b)"
scan local
check "the post-scan task keeps the stub" "$stub_direct" "$(stub_item local "$DIRECT")"
check "the state stays on the stub" "$(echo "$data" | jq -c '{Played, LastPlayedDate}')" "$(user_data "$stub_direct" | jq -c '{Played, LastPlayedDate}')"
echo "watch state on the copy: $(user_data "$local_direct")"

echo "--- the next sync: the local copy joins the group of the stub as its primary, and only played is aligned (Q43, D4)"
position=$(user_data "$stub_direct" | jq .PlaybackPositionTicks)
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
sync_now local
check "the local copy is the primary" "$(vid "/media/film/$DIRECT")" "$(primary_of "$(vid "$(server_folder "$(cat "$WORK/id-remote")")/Film/$DIRECT")")"
check "the copy is played too" true "$(user_data "$local_direct" | jq .Played)"
check "the stub keeps its position" "$position" "$(user_data "$stub_direct" | jq .PlaybackPositionTicks)"
check "and its state" "$(echo "$data" | jq -c '{Played, LastPlayedDate}')" "$(user_data "$stub_direct" | jq -c '{Played, LastPlayedDate}')"
exit $FAILED
