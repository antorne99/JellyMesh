#!/usr/bin/env bash
#
# Phase 0, point 4: proxying a remote PlaybackInfo with a forwarded device
# profile. Covers the decision per profile, HLS URL forms, stop calls, concurrent
# streams, seek, audio change, subtitles, and a rewriting proxy played end to end.
#
# Needs the environment running, plus curl, jq and python3. ffprobe on the host
# is optional, used only to show audio codecs. Reads remoto-standard as the
# non-admin user with its own DeviceId. The container is only read, via /proc and
# ls, because it has no ps. Every transcode opened here is stopped by
# PlaySessionId, so nothing is left in /cache/transcodes.
#
# Deliberately not automated: a URL without PlaySessionId. That job cannot be
# stopped through the API, so its files stay until the daily cleanup task.
#
# "private" changes server settings, so it starts its own throwaway container on
# PRIVATE_PORT, playing the part of a remote with bad settings and of the local
# server of a .strm. It takes about 8 minutes, needs 2 GB of disk, and removes
# everything at the end. It is not part of "all".
#
# Usage: playbackinfo-proxy.sh [matrix|hls|stop|concurrent|seek|audio|subs|proxy|private|all]

set -euo pipefail

SPIKES=$(cd "$(dirname "$0")" && pwd)
cd "$SPIKES/.."
source scripts/common.sh

DEVICE_ID=${DEVICE_ID:-jm-e3-p4-spike}
PROXY_PORT=${PROXY_PORT:-18230}
PRIVATE_PORT=${PRIVATE_PORT:-18231}
PRIVATE=jm-e3-p4
CONTAINER=jellymesh-test-remoto-standard-1
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
LOCAL_TOKEN=local-user-token # must match rewriting-proxy.py
AUTH="MediaBrowser Client=\"JellyMeshSpike\", Device=\"p4\", DeviceId=\"$DEVICE_ID\", Version=\"0.1\""
WORK=$(mktemp -d)
PROXY_PID=

cleanup() {
  [ -z "$PROXY_PID" ] || kill "$PROXY_PID" 2>/dev/null || true
  docker rm -f -v "$PRIVATE" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

use_instance remoto-standard
login "$REMOTE_USER" "$REMOTE_PASSWORD"
USER_ID=$(get /Users/Me | jq -r .Id)

item_id() { # <title>
  get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=$(jq -rn --arg t "$1" '$t|@uri')" | jq -r '.Items[0].Id'
}
AV=$(item_id "JM Audio e Sottotitoli")
CODEC=$(item_id "JM Confronto Codec")
HDR=$(item_id "JM Confronto HDR")

profile() { # <web|android|tv|web-hls-subs>
  local subs
  case $1 in
    web|web-hls-subs)
      [ "$1" = web ] && subs='[{"Format":"vtt","Method":"External"},{"Format":"srt","Method":"External"},{"Format":"ass","Method":"External"}]' \
        || subs='[{"Format":"vtt","Method":"Hls"}]'
      jq -n --argjson subs "$subs" '{Name:"web",MaxStreamingBitrate:120000000,MaxStaticBitrate:100000000,
        DirectPlayProfiles:[
          {Container:"webm",Type:"Video",VideoCodec:"vp8,vp9,av1",AudioCodec:"vorbis,opus"},
          {Container:"mp4,m4v",Type:"Video",VideoCodec:"h264,vp9,av1",AudioCodec:"aac,mp3,opus,flac"}],
        TranscodingProfiles:[
          {Container:"mp4",Type:"Video",VideoCodec:"h264,vp9,av1",AudioCodec:"aac,mp3,opus",Context:"Streaming",Protocol:"hls",MaxAudioChannels:"2",MinSegments:1,BreakOnNonKeyFrames:true},
          {Container:"ts",Type:"Video",VideoCodec:"h264",AudioCodec:"aac,mp3",Context:"Streaming",Protocol:"hls",MaxAudioChannels:"2",MinSegments:1,BreakOnNonKeyFrames:true}],
        CodecProfiles:[],SubtitleProfiles:$subs}'
      ;;
    android)
      jq -n '{Name:"android",MaxStreamingBitrate:100000000,MaxStaticBitrate:100000000,
        DirectPlayProfiles:[{Container:"mkv,mp4,webm,m4v,mov,ts,avi",Type:"Video",VideoCodec:"h264,hevc,av1,vp9,vp8,mpeg4,mpeg2video",AudioCodec:"aac,ac3,eac3,dts,truehd,mp3,flac,opus,vorbis"}],
        TranscodingProfiles:[{Container:"ts",Type:"Video",VideoCodec:"h264,hevc",AudioCodec:"aac,ac3,mp3",Context:"Streaming",Protocol:"hls",MaxAudioChannels:"6",MinSegments:1,BreakOnNonKeyFrames:false}],
        CodecProfiles:[],
        SubtitleProfiles:[{Format:"srt",Method:"Embed"},{Format:"ass",Method:"Embed"},{Format:"subrip",Method:"Embed"},{Format:"vtt",Method:"External"},{Format:"srt",Method:"External"}]}'
      ;;
    tv)
      jq -n '{Name:"tv",MaxStreamingBitrate:20000000,MaxStaticBitrate:20000000,
        DirectPlayProfiles:[{Container:"mp4,m4v",Type:"Video",VideoCodec:"h264",AudioCodec:"aac,ac3"}],
        TranscodingProfiles:[{Container:"ts",Type:"Video",VideoCodec:"h264",AudioCodec:"aac,ac3",Context:"Streaming",Protocol:"hls",MaxAudioChannels:"2",MinSegments:1,BreakOnNonKeyFrames:false}],
        CodecProfiles:[{Type:"Video",Codec:"h264",Conditions:[
          {Condition:"LessThanEqual",Property:"Width",Value:"1920",IsRequired:true},
          {Condition:"LessThanEqual",Property:"VideoLevel",Value:"41",IsRequired:false},
          {Condition:"EqualsAny",Property:"VideoRangeType",Value:"SDR",IsRequired:true}]}],
        SubtitleProfiles:[{Format:"vtt",Method:"External"},{Format:"srt",Method:"External"}]}'
      ;;
  esac
}

playback_info() { # <item> <profile> [extra json]
  post "/Items/$1/PlaybackInfo" "$(jq -n --argjson p "$(profile "$2")" --arg u "$USER_ID" --argjson x "${3:-null}" \
    '{UserId:$u,DeviceProfile:$p}+($x//{})')"
}

# open <name> <item> <profile> [extra]: PlaybackInfo plus master and main playlists.
open_hls() {
  local name=$1
  playback_info "$2" "$3" "${4:-}" >"$WORK/$name.json"
  local url dir
  url=$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/$name.json")
  dir=${url%%\?*}; dir=${dir%/*}
  echo "$dir" >"$WORK/$name.dir"
  # ApiKey dropped: header auth keeps the token out of master and main playlists.
  get "$(sed -E 's/&ApiKey=[^&]*//' <<<"$url")" >"$WORK/$name.master"
  get "$dir/$(grep -v '^#' "$WORK/$name.master" | tail -1)" >"$WORK/$name.main"
}

play_session() { jq -r .PlaySessionId "$WORK/$1.json"; }

segment() { # <name> <n> [curl args]
  local name=$1 n=$2 line
  shift 2
  line=$(grep -v '^#' "$WORK/$name.main" | grep -E "/$n\.[a-z0-9]+\?")
  curl -sS -H "$(auth_header)" -o "$WORK/$name.seg$n" -w "segment $n: %{http_code}, %{size_download} bytes, %{time_total}s\n" \
    "$@" "$BASE$(cat "$WORK/$name.dir")/$line"
}

# Never send ItemId here: Stopped with an item and no position marks it played
# for the remote user, while PlaySessionId alone only closes the transcode.
stopped() { # [play session]
  post /Sessions/Playing/Stopped "$(jq -n --arg p "${1:-}" 'if $p=="" then {} else {PlaySessionId:$p} end')"
}

# What is really running on the remote: ffmpeg processes and transcode files.
transcodes() {
  docker exec -i "$CONTAINER" sh -s <<'EOF'
for p in /proc/[0-9]*; do
  [ "$(readlink $p/exe 2>/dev/null)" = /usr/lib/jellyfin-ffmpeg/ffmpeg ] || continue
  c=$(tr '\000' ' ' <$p/cmdline 2>/dev/null)
  echo "  ffmpeg ${p#/proc/}:$(echo "$c" | grep -oE ' -ss [0-9:.]+| -start_number [0-9]+| -codec:[av]:0 [a-z0-9_]+' | tr -d '\n')"
done
echo "  $(ls /cache/transcodes | wc -l) file(s) in /cache/transcodes"
EOF
}

audio_codec() {
  command -v ffprobe >/dev/null || return 0
  ffprobe -v error -select_streams a -show_entries stream=codec_name -of csv=p=0 "$1" | sort -u | tr -d ',\n'
  echo
}

matrix() {
  echo "=== Decision per profile. mkv and HEVC on tv and web force HLS."
  local item profile
  for item in "$AV" "$CODEC" "$HDR"; do
    for profile in web android tv; do
      playback_info "$item" "$profile" | jq -r --arg p "$profile" '.MediaSources[0] as $s |
        "\($s.Name) / \($p): direct play \($s.SupportsDirectPlay), HLS \($s.TranscodingUrl != null), " +
        "reasons \($s.TranscodingUrl // "" | capture("TranscodeReasons=(?<r>[^&]*)").r? // "-")"'
    done
  done
  echo "=== Refused: no transcoding allowed in the request"
  playback_info "$HDR" tv '{"EnableTranscoding":false}' |
    jq -c '{ErrorCode, source: (.MediaSources[0] | {SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding, TranscodingUrl})}'
}

hls() {
  echo "=== HLS forms. TranscodingUrl as returned:"
  open_hls a "$HDR" tv
  jq -r '.MediaSources[0].TranscodingUrl' "$WORK/a.json"
  echo "=== master with header auth, no ApiKey sent:"
  cat "$WORK/a.master"
  echo "=== main, first lines:"
  sed -n 1,7p "$WORK/a.main"
  echo "=== ApiKey in main: $(grep -c ApiKey "$WORK/a.main" || true)"
  echo "=== ffmpeg starts at the first segment, not at the playlists:"
  transcodes
  segment a 0
  transcodes
  stopped "$(play_session a)"
  transcodes
}

stops() {
  echo "=== Stopped with PlaySessionId on a running transcode"
  open_hls a "$HDR" tv; segment a 0; transcodes
  stopped "$(play_session a)"; transcodes
  echo "=== DELETE ActiveEncodings on a running transcode, non-admin"
  open_hls b "$HDR" tv; segment b 0; transcodes
  curl -sS -o /dev/null -w 'delete: %{http_code}\n' -X DELETE -H "$(auth_header)" \
    "$BASE/Videos/ActiveEncodings?DeviceId=$DEVICE_ID&PlaySessionId=$(play_session b)"
  sleep 3
  transcodes
}

concurrent() {
  echo "=== Two transcodes, same title, same token, same DeviceId"
  open_hls a "$HDR" tv; open_hls b "$HDR" tv
  segment a 0 & segment b 0 & wait
  transcodes
  echo "=== Stopped without PlaySessionId kills nothing"
  stopped; transcodes
  echo "=== Stopped on one session leaves the other"
  stopped "$(play_session a)"; transcodes
  segment b 3
  stopped "$(play_session b)"; transcodes
  echo "=== User-Agent is part of the output hash: a different one starts a new ffmpeg"
  open_hls c "$HDR" tv; segment c 0; transcodes
  segment c 1 -A OtherAgent/1.0; transcodes
  stopped "$(play_session c)"; transcodes
  echo "=== Those are the orphans of the first ffmpeg. Reopening its hash lets Stopped remove them."
  segment c 15; stopped "$(play_session c)"; transcodes
}

seek() {
  echo "=== StartTimeTicks does not reach an HLS URL"
  open_hls a "$HDR" tv '{"StartTimeTicks":300000000}'
  echo "StartTimeTicks in TranscodingUrl: $(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/a.json" | grep -c StartTimeTicks || true)"
  echo "Segments in main: $(grep -c EXTINF "$WORK/a.main")"
  echo "=== Seek is the client asking for a later segment"
  segment a 15; transcodes
  segment a 2; transcodes
  stopped "$(play_session a)"; transcodes
  echo "=== After a stop, a late segment request starts ffmpeg again"
  segment a 15; transcodes
  stopped "$(play_session a)"; transcodes
  echo "=== Direct stream"
  local stream="$BASE/Videos/$AV/stream?static=true&MediaSourceId=$AV"
  curl -sS -I -H "$(auth_header)" "$stream" | grep -iE '^HTTP|content-length|accept-ranges'
  curl -sS -D - -o /dev/null -H "$(auth_header)" -H 'Range: bytes=1000000-1000999' "$stream" | grep -iE '^HTTP|content-(length|range)'
  curl -sS -D - -o /dev/null -H "$(auth_header)" -H 'Range: bytes=99999999-' "$stream" | grep -iE '^HTTP|content-range'
  echo "Same without any auth: $(curl -sS -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "$stream")"
}

audio() {
  local nocopy='{"AllowVideoStreamCopy":false}'
  echo "=== AudioStreamIndex without MediaSourceId is ignored"
  playback_info "$AV" tv '{"AudioStreamIndex":4}' | jq -r '.MediaSources[0].TranscodingUrl' | grep -oE 'AudioStreamIndex=[0-9]+'
  echo "=== New PlaybackInfo with it, resume at segment 5, then stop the old session"
  open_hls a "$AV" tv "$nocopy"; segment a 0
  open_hls b "$AV" tv "{\"AllowVideoStreamCopy\":false,\"AudioStreamIndex\":4,\"MediaSourceId\":\"$AV\"}"
  segment b 5; transcodes
  echo "old: $(audio_codec "$WORK/a.seg0"), new: $(audio_codec "$WORK/b.seg5")"
  stopped "$(play_session a)"; transcodes
  segment b 6
  stopped "$(play_session b)"; transcodes
  echo "=== Android: the choice shows only in DefaultAudioStreamIndex, direct play has no URL to change"
  playback_info "$AV" android "{\"AudioStreamIndex\":4,\"MediaSourceId\":\"$AV\"}" |
    jq -c '.MediaSources[0] | {SupportsDirectPlay, TranscodingUrl, DefaultAudioStreamIndex}'
}

subs() {
  local index base
  base=$(playback_info "$AV" web | jq -r '[.MediaSources[0].MediaStreams[] | select(.DeliveryUrl)][0].DeliveryUrl' | sed -E 's#/Subtitles/.*##')
  for profile_name in web android tv web-hls-subs; do
    echo "=== $profile_name"
    playback_info "$AV" "$profile_name" |
      jq -c '.MediaSources[0].MediaStreams[] | select(.Type=="Subtitle") | {Index, IsExternal, DeliveryMethod, DeliveryUrl}' |
      sed -E 's/ApiKey=[0-9a-f]+/ApiKey=<token>/'
  done
  for index in 0 1 5; do
    echo "index $index: token $(curl -sS -o /dev/null -w '%{http_code}' -H "$(auth_header)" "$BASE$base/Subtitles/$index/0/Stream.vtt")," \
      "no auth $(curl -sS -o /dev/null -w '%{http_code}' "$BASE$base/Subtitles/$index/0/Stream.vtt")"
  done
}

proxy_request() { # <item> <profile> [extra]
  jq -n --argjson p "$(profile "$2")" --arg u "$USER_ID" --arg i "$1" --argjson x "${3:-null}" '{item:$i,user:$u,profile:$p}+($x//{})' |
    curl -fsS -X POST -H "X-Emby-Token: $LOCAL_TOKEN" -d @- "http://127.0.0.1:$PROXY_PORT/open"
}

proxy_stop() {
  curl -fsS -o /dev/null -X POST -H "X-Emby-Token: $LOCAL_TOKEN" "http://127.0.0.1:$PROXY_PORT/p/$1/stop"
}

client_ffmpeg() {
  docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg --add-host host.docker.internal:host-gateway "$IMAGE" \
    -hide_banner -loglevel error -stats "$@" 2>&1 | tr '\r' '\n' | tail -1
}

proxy() {
  start_proxy
  local client="http://host.docker.internal:$PROXY_PORT" sid
  echo "=== No local auth: $(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PROXY_PORT/p/x/master.m3u8")"
  echo "=== Transcode, TS segments"
  sid=$(proxy_request "$HDR" tv | jq -r .Sid)
  client_ffmpeg -i "$client/p/$sid/master.m3u8?ApiKey=$LOCAL_TOKEN" -map 0:v -f null -
  proxy_stop "$sid"; transcodes
  echo "=== Remux, fMP4 with EXT-X-MAP"
  sid=$(proxy_request "$CODEC" web | jq -r .Sid)
  client_ffmpeg -i "$client/p/$sid/master.m3u8?ApiKey=$LOCAL_TOKEN" -map 0:v -f null -
  proxy_stop "$sid"; transcodes
  echo "=== HLS subtitles"
  sid=$(proxy_request "$AV" web-hls-subs "{\"subtitle\":1}" | jq -r .Sid)
  local master uri cue
  master=$(curl -fsS -H "X-Emby-Token: $LOCAL_TOKEN" "http://127.0.0.1:$PROXY_PORT/p/$sid/master.m3u8")
  uri=$(grep -oE 'URI="[^"]*' <<<"$master" | sed -n 2p | cut -c6-)
  cue=$(curl -fsS -H "X-Emby-Token: $LOCAL_TOKEN" "http://127.0.0.1:$PROXY_PORT$uri" | grep -v '^#' | head -1)
  curl -fsS -H "X-Emby-Token: $LOCAL_TOKEN" "http://127.0.0.1:$PROXY_PORT$cue" | sed -n 4,5p
  proxy_stop "$sid"
  echo "=== Direct stream, Range and ffmpeg -ss"
  sid=$(proxy_request "$AV" android | jq -r .Sid)
  curl -sS -D - -o /dev/null -H "X-Emby-Token: $LOCAL_TOKEN" -H 'Range: bytes=1000000-1000999' \
    "http://127.0.0.1:$PROXY_PORT/p/$sid/stream" | grep -iE '^HTTP|content-(length|range)'
  client_ffmpeg -ss 30 -headers "X-Emby-Token: $LOCAL_TOKEN"$'\r\n' -i "$client/p/$sid/stream" -t 5 -map 0:v -f null -
  for index in 0 1; do
    curl -fsS -H "X-Emby-Token: $LOCAL_TOKEN" "http://127.0.0.1:$PROXY_PORT/p/$sid/sub/$index.vtt" | sed -n 4p
  done
  proxy_stop "$sid"
  echo "=== Leak check on every byte sent to clients. All must be 0."
  local needle
  for needle in "$TOKEN" ":8097" 127.0.0.1 remoto-standard 172.20. /media/ "$DEVICE_ID" PlaySessionId ApiKey=[0-9a-f]; do
    echo "$needle: $(LC_ALL=C grep -a -c -E "$needle" "$WORK/capture" || true)"
  done
  echo "Captured $(wc -c <"$WORK/capture") bytes."
}

start_proxy() {
  python3 "$SPIKES/rewriting-proxy.py" "$PROXY_PORT" "$BASE" "$TOKEN" "$WORK/capture" >"$WORK/proxy.log" 2>&1 &
  PROXY_PID=$!
  sleep 1
}

start_private() {
  local media=${MEDIA_ROOT:-$PWD/media}/remoto-standard
  mkdir -p "$WORK/long/film/Lungo (2026)" "$WORK/long/film/Via Strm (2026)"
  docker run --rm --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg -v "$media:/media:ro" -v "$WORK/long:/long" "$IMAGE" \
    -hide_banner -loglevel error -y -stream_loop 29 -i "/media/film/JM Confronto HDR (2026)/JM Confronto HDR (2026).mkv" \
    -c copy "/long/film/Lungo (2026)/Lungo (2026).mkv"
  echo "http://host.docker.internal:$PROXY_PORT/strm/$1" >"$WORK/long/film/Via Strm (2026)/Via Strm (2026).strm"
  docker run -d --rm --name "$PRIVATE" -p "127.0.0.1:$PRIVATE_PORT:8096" --add-host host.docker.internal:host-gateway \
    -v "$media:/media:ro" -v "$WORK/long:/long:ro" "$IMAGE" >/dev/null
  BASE=http://127.0.0.1:$PRIVATE_PORT
  CONTAINER=$PRIVATE
  until curl -fsS "$BASE/System/Info/Public" 2>/dev/null | grep -q Version; do sleep 1; done
  TOKEN=
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
  get /Startup/User >/dev/null
  post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
  post /Startup/RemoteAccess '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
  post /Startup/Complete >/dev/null
  login "$ADMIN_USER" "$ADMIN_PASSWORD"
  ADMIN_TOKEN=$TOKEN
  post "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=true" \
    '{"LibraryOptions":{"PathInfos":[{"Path":"/long/film"},{"Path":"/media/film"}],"TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"ImageFetchers":[]}]}}' >/dev/null
  post /Users/New '{"Name":"jellymesh","Password":"jellymesh-test"}' >/dev/null
  AUTH=${AUTH/$DEVICE_ID/$DEVICE_ID-user}
  login "$REMOTE_USER" "$REMOTE_PASSWORD"
  USER_ID=$(get /Users/Me | jq -r .Id)
  until [ "$(item_id "Via Strm")" != null ]; do sleep 2; done
  until [ "$(get /Items/Counts | jq .MovieCount)" -ge 10 ]; do sleep 2; done
  LONG=$(item_id "Lungo")
  HDR=$(item_id "JM Confronto HDR")
}

as_admin() { # <function and args>
  local user_token=$TOKEN
  TOKEN=$ADMIN_TOKEN
  "$@"
  TOKEN=$user_token
}

policy() { # <json merged into the user's policy>
  local merged
  merged=$(get "/Users/$USER_ID" | jq --argjson x "$1" '.Policy+$x')
  post "/Users/$USER_ID/Policy" "$merged" >/dev/null
}

outcome() { # What a client of the remote gets: decision, master, first segment.
  jq -c '{ErrorCode, source: (.MediaSources[0] | {SupportsDirectPlay, SupportsTranscoding, Hls: (.TranscodingUrl != null)})}' \
    <<<"$(playback_info "$HDR" tv)"
  [ "$(playback_info "$HDR" tv | jq -r '.MediaSources[0].TranscodingUrl')" != null ] || return 0
  open_hls s "$HDR" tv
  grep -oE 'CODECS="[^"]*"' "$WORK/s.master"
  segment s 0 --max-time 30 || true
  stopped "$(play_session s)"
}

private() {
  start_proxy
  local sid
  sid=$(proxy_request "$AV" android | jq -r .Sid)
  start_private "$sid"
  echo "=== Client vanishes after one segment, no start report and no stop"
  open_hls a "$LONG" tv; segment a 0
  local elapsed=0
  while [[ $(transcodes) == *ffmpeg* ]]; do sleep 5; elapsed=$((elapsed + 5)); done
  echo "ffmpeg gone about ${elapsed}s after the last request"
  sleep 3 # files are deleted 1.5 s after the kill
  transcodes
  echo "=== Same URLs after the job was reaped"
  segment a 40; transcodes
  stopped "$(play_session a)"
  echo "=== Ping alone keeps it alive past the timeout"
  open_hls b "$LONG" tv; segment b 0
  for _ in 1 2 3 4; do
    sleep 25
    post "/Sessions/Playing/Ping?playSessionId=$(play_session b)" >/dev/null
  done
  transcodes
  stopped "$(play_session b)"
  echo "=== Signals. Baseline:"; outcome
  echo "=== User may not transcode video: HEVC is copied to a client that cannot play it"
  as_admin policy '{"EnableVideoPlaybackTranscoding":false}'; outcome
  echo "=== Nor audio, nor remux"
  as_admin policy '{"EnableAudioPlaybackTranscoding":false,"EnablePlaybackRemuxing":false}'; outcome
  as_admin policy '{"EnableVideoPlaybackTranscoding":true,"EnableAudioPlaybackTranscoding":true,"EnablePlaybackRemuxing":true}'
  echo "=== VAAPI configured with no device"
  local encoding
  encoding=$(get /System/Configuration/encoding)
  as_admin post /System/Configuration/encoding "$(jq '.HardwareAccelerationType="vaapi"|.VaapiDevice="/dev/dri/renderD128"|.EnableHardwareEncoding=true' <<<"$encoding")" >/dev/null
  outcome
  as_admin post /System/Configuration/encoding "$encoding" >/dev/null
  echo "=== Non-admin reads the remote's acceleration type: $(get /System/Configuration/encoding | jq -r .HardwareAccelerationType)"
  echo "=== .strm through the proxy, private instance as the local server"
  local strm
  strm=$(item_id "Via Strm")
  playback_info "$strm" web | jq -c '.MediaSources[0] | {Protocol, Path, RunTimeTicks, SupportsDirectPlay}'
  curl -sS -D - -o /dev/null -H "$(auth_header)" -H 'Range: bytes=1000000-1000999' "$BASE/Videos/$strm/stream?static=true" | grep -iE '^HTTP|content-range'
  curl -sS -D - -o /dev/null -H 'User-Agent: Mozilla/5.0' -H 'Range: bytes=0-999' "http://127.0.0.1:$PROXY_PORT/strm/$sid" | grep -iE '^HTTP|content-range'
  grep relay "$WORK/proxy.log" | sed -E 's/^[^ ]+ //'
}

case ${1:-all} in
  matrix) matrix ;;
  hls) hls ;;
  stop) stops ;;
  concurrent) concurrent ;;
  seek) seek ;;
  audio) audio ;;
  subs) subs ;;
  proxy) proxy ;;
  private) private ;;
  all) matrix; hls; stops; concurrent; seek; audio; subs; proxy ;;
  *) echo "usage: $0 [matrix|hls|stop|concurrent|seek|audio|subs|proxy|private|all]" >&2; exit 1 ;;
esac
