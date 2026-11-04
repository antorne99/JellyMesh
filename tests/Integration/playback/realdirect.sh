#!/usr/bin/env bash
# Direct path against the two real remotes, with an Android-like profile that lets the remote answer direct play.
source "$(dirname "$0")/lib.sh"
G=$S/run
AUTHB='MediaBrowser Client="JellyMeshPlay", Device="android", DeviceId="jm-play-android", Version="0.1"'
login viewer
P='{"DeviceProfile":{"Name":"android","MaxStreamingBitrate":100000000,"MaxStaticBitrate":100000000,"DirectPlayProfiles":[{"Container":"mkv,mp4,webm,m4v,mov,ts,avi","Type":"Video","VideoCodec":"h264,hevc,av1,vp9,vp8,mpeg4,mpeg2video","AudioCodec":"aac,ac3,eac3,dts,truehd,mp3,flac,opus,vorbis"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264,hevc","AudioCodec":"aac,ac3,mp3","Context":"Streaming","Protocol":"hls","MaxAudioChannels":"6","MinSegments":1,"BreakOnNonKeyFrames":false}],"CodecProfiles":[],"SubtitleProfiles":[{"Format":"srt","Method":"Embed"},{"Format":"vtt","Method":"External"},{"Format":"srt","Method":"External"}]}}'
for t in "Tears of Steel" "JM Solo JellyMesh"; do
  echo "== $t"
  IT=$(get "/Items?Recursive=true&IncludeItemTypes=Movie&SearchTerm=$(jq -rn --arg t "$t" '$t|@uri')" | jq -r '.Items[0].Id')
  curl -fsS -X POST -H "$ACCEPT" -H "$(hdr)" -H 'Content-Type: application/json' -d "$P" "$BASE/Items/$IT/PlaybackInfo" > "$G/rd.json"
  jq -c '.MediaSources[0]|{Path,SupportsDirectPlay,SupportsDirectStream,SupportsTranscoding,TranscodingSubProtocol,TranscodingContainer,Size}' "$G/rd.json"
  U=$(jq -r '.MediaSources[0].TranscodingUrl' "$G/rd.json"); SZ=$(jq -r '.MediaSources[0].Size' "$G/rd.json")
  curl -sS -o /dev/null -D - -H 'Range: bytes=1000-1999' "$BASE$U" | tr -d '\r' | grep -iE '^HTTP|content-range|content-length|accept-ranges|^server|^date' | tr '\n' ' '; echo
  curl -sS -o /dev/null -w 'past end: %{http_code}\n' -H "Range: bytes=$((SZ+10))-" "$BASE$U"
  curl -sS -o /dev/null -w 'HEAD: %{http_code}\n' -I "$BASE$U"
  curl -sS -o /dev/null -w 'no range: %{http_code} %{size_download}\n' -r 0-1048575 "$BASE$U"
  docker exec $NAME /usr/lib/jellyfin-ffmpeg/ffmpeg -v error -ss 30 -t 2 -i "http://127.0.0.1:8096${U}" -map 0:v:0 -f null - 2>&1 | tail -2
  docker exec $NAME /usr/lib/jellyfin-ffmpeg/ffprobe -v error -show_entries format=duration -of csv=p=0 "http://127.0.0.1:8096${U}"
  curl -sS -o /dev/null -w 'DELETE: %{http_code}\n' -X DELETE -H "$(hdr)" "$BASE/Videos/ActiveEncodings?deviceId=x&playSessionId=$(jq -r .PlaySessionId "$G/rd.json")"
  curl -sS -o /dev/null -w 'after close: %{http_code}\n' "$BASE$U"
done
