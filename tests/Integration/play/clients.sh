#!/usr/bin/env bash
#
# Calls the audio, book and photo routes the way the main clients do, against any
# server URL, and prints one line per request: what the server answered, how many
# bytes, and their digest. It asserts nothing: later phases compare a stock server
# with the federated copy of the same item, or check a line against what they expect.
#
#   play/clients.sh <base URL> <user> <password> <command> <item id or exact name> [--file <path>]
#
# Commands, with the client and source they copy (README, "Client routes"):
#   web-audio      jellyfin-web <audio>: HEAD then GET of /Audio/{id}/universal with
#                  ApiKey in the query, "Range: bytes=0-" and a seek; follows HLS
#   web-audio-hls  the same with a 64 kbit/s cap, so the server transcodes to HLS
#   finamp-file    Finamp direct play: /Items/{id}/File?ApiKey, then a mid-file Range
#   finamp-hls     Finamp transcode: /Audio/{id}/main.m3u8, AAC in fMP4, first segment
#   stream         older clients: /Audio/{id}/stream?static=true with the auth header
#   playbackinfo   POST /Items/{id}/PlaybackInfo with a browser audio profile
#   lyrics         GET /Audio/{id}/Lyrics
#   download       the web "Download" button: /Items/{id}/Download?ApiKey
#   epub           epub.js: one GET of the download URL
#   comic          the comics reader: one fetch of the whole file
#   pdf            pdf.js: a GET dropped after the headers, then 64 KiB Range chunks
#                  from the start and the end, when the server offers ranges
#   jellybook      JellyBook: /Items/{id}/Download with the auth header
#   photo-web      the web grid (anonymous Primary image), then the slideshow:
#                  Download?ApiKey, and Primary at quality 100 for users without downloads
#   photo-tv       Android TV: anonymous Primary image fitted to 1920x1080
#
# --file compares each body with that local file: a 200 with the whole file, a 206
# with the slice its Content-Range names. OUT=<dir> keeps every header and body.
# Output columns: client, step, method, status, content type, length, content range,
# accept ranges, bytes received, sha256 (first 16), compared with --file.

set -euo pipefail

usage() {
  sed -n '3,30p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}
[ $# -ge 5 ] || usage
BASE=${1%/} USER_NAME=$2 USER_PW=$3 COMMAND=$4 ITEM=$5
shift 5
FILE=
while [ $# -gt 0 ]; do
  case $1 in
    --file) FILE=$2; shift 2 ;;
    *) usage ;;
  esac
done
[ -z "$FILE" ] || [ -f "$FILE" ] || { echo "no file $FILE" >&2; exit 2; }

OUT=${OUT:-}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
[ -z "$OUT" ] || mkdir -p "$OUT"
ACCEPT='Accept: application/json; profile="PascalCase"'
SESSION=jm$$$(date +%s)
STEP=0
FAILED=0

sha() {
  if command -v sha256sum >/dev/null; then sha256sum | cut -c1-64; else shasum -a 256 | cut -c1-64; fi
}

uri() {
  jq -rn --arg v "$1" '$v | @uri'
}

# Authorization value of a client, with a token when given.
auth_of() {
  local client=$1 token=${2:-} device id version
  case $client in
    web) client="Jellyfin Web" device=Firefox version=10.11.0 ;;
    finamp) client=Finamp device="Pixel 8" version=0.9.20 ;;
    jellybook) client=JellyBook device="Pixel 8" version=1.2.6 ;;
    tv) client="Android TV" device="NVIDIA SHIELD" version=0.19.0 ;;
    legacy) client=Symfonium device="Pixel 8" version=13.0.0 ;;
  esac
  id=$(printf 'jellymesh-%s' "$1" | base64 | tr -d '=\n')
  printf 'MediaBrowser Client="%s", Device="%s", DeviceId="%s", Version="%s"' "$client" "$device" "$id" "$version"
  [ -z "$token" ] || printf ', Token="%s"' "$token"
}

# login <client>: sets TOKEN and USER_ID for that client's own session.
login() {
  local reply
  reply=$(curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $(auth_of "$1")" \
    -d "$(jq -n --arg u "$USER_NAME" --arg p "$USER_PW" '{Username: $u, Pw: $p}')" "$BASE/Users/AuthenticateByName")
  TOKEN=$(echo "$reply" | jq -r '.AccessToken // empty' 2>/dev/null || true)
  USER_ID=$(echo "$reply" | jq -r '.User.Id // empty' 2>/dev/null || true)
  [ -n "$TOKEN" ] || { echo "login as $USER_NAME failed on $BASE" >&2; exit 1; }
  CLIENT=$1
  AUTH="Authorization: $(auth_of "$1" "$TOKEN")"
}

# resolve: sets ID and TAG (primary image tag) from an id or an exact item name.
resolve() {
  local found
  if [[ $ITEM =~ ^[0-9a-f]{32}$ ]]; then
    found=$(curl -sS -H "$ACCEPT" -H "$AUTH" "$BASE/Items?userId=$USER_ID&ids=$ITEM&Fields=Path")
  else
    found=$(curl -sS -H "$ACCEPT" -H "$AUTH" \
      "$BASE/Items?userId=$USER_ID&Recursive=true&searchTerm=$(uri "$ITEM")&Fields=Path&IncludeItemTypes=Audio,AudioBook,Book,Photo,MusicVideo,Video,Movie,Episode")
  fi
  found=$(echo "$found" | jq -c --arg n "$ITEM" '[.Items[] | select(.Id == $n or .Name == $n)]')
  [ "$(echo "$found" | jq length)" -gt 0 ] || { echo "no item $ITEM on $BASE" >&2; exit 1; }
  [ "$(echo "$found" | jq length)" -eq 1 ] || echo "$ITEM: $(echo "$found" | jq length) items, using the first" >&2
  ID=$(echo "$found" | jq -r '.[0].Id')
  TAG=$(echo "$found" | jq -r '.[0].ImageTags.Primary // ""')
}

header() {
  tr -d '\r' <"$1" | grep -i "^$2:" | tail -1 | cut -d: -f2- | sed 's/^ *//' || true
}

# compare <body> <status> <content range>: match, differs or "-" against --file.
compare() {
  local body=$1 status=$2 range=$3 from to
  [ -n "$FILE" ] && [ -s "$body" ] || { echo -; return; }
  if [ "$status" = 200 ]; then
    [ "$(sha <"$body")" = "$(sha <"$FILE")" ] && echo match || echo differs
  elif [ "$status" = 206 ] && [[ $range =~ bytes\ ([0-9]+)-([0-9]+)/ ]]; then
    from=${BASH_REMATCH[1]} to=${BASH_REMATCH[2]}
    [ "$(sha <"$body")" = "$(tail -c +$((from + 1)) "$FILE" | head -c $((to - from + 1)) | sha)" ] && echo match || echo differs
  else
    echo -
  fi
}

# req <step> <method> <path> [curl args...]: one request, one output line. Leaves the
# body in $BODY and the headers in $HEADERS for the caller.
req() {
  local step=$1 method=$2 path=$3 status args
  shift 3
  STEP=$((STEP + 1))
  BODY=$WORK/$STEP.body HEADERS=$WORK/$STEP.headers
  args=(-sS -L -D "$HEADERS" -o "$BODY" -w '%{http_code}')
  [ "$method" = HEAD ] && args+=(-I) || args+=(-X "$method")
  status=$(curl "${args[@]}" "$@" "$BASE$path" 2>"$WORK/curl") || true
  [ -f "$BODY" ] && [ "$method" != HEAD ] || : >"$BODY"
  [ -f "$HEADERS" ] || : >"$HEADERS"
  if [ -s "$WORK/curl" ] && ! grep -qE 'Failure writing output|Maximum file size' "$WORK/curl"; then
    echo "$step: $(cat "$WORK/curl")" >&2
  fi
  [ "$status" != 000 ] || FAILED=1
  LAST_STATUS=$status
  local range
  range=$(header "$HEADERS" Content-Range)
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$CLIENT" "$step" "$method" "$status" \
    "$(header "$HEADERS" Content-Type)" "$(header "$HEADERS" Content-Length)" "${range:--}" \
    "$(header "$HEADERS" Accept-Ranges)" "$(wc -c <"$BODY" | tr -d ' ')" "$(sha <"$BODY" | cut -c1-16)" \
    "$(compare "$BODY" "$status" "$range")"
  if [ -n "$OUT" ]; then
    cp "$HEADERS" "$OUT/$(printf %02d "$STEP")-$step.headers"
    cp "$BODY" "$OUT/$(printf %02d "$STEP")-$step.body"
  fi
}

# total_of <headers>: the full size, from Content-Range or Content-Length.
total_of() {
  local range
  range=$(header "$1" Content-Range)
  if [ -n "$range" ]; then echo "${range##*/}"; else header "$1" Content-Length; fi
}

# follow_hls <step> <path of a playlist>: the media playlist, its init segment and first segment.
follow_hls() {
  local step=$1 path=$2 dir line map depth
  for depth in 1 2; do
    dir=${path%%\?*}
    dir=${dir%/*}
    map=$(grep -o 'URI="[^"]*"' "$BODY" | head -1 | sed 's/^URI="//; s/"$//' || true)
    line=$(grep -v '^#' "$BODY" | grep -v '^[[:space:]]*$' | head -1 || true)
    [ -n "$line" ] || return 0
    if [ -n "$map" ]; then
      req "$step-init" GET "$(relative "$dir" "$map")"
    fi
    path=$(relative "$dir" "$line")
    if [[ $line == *.m3u8* ]]; then
      req "$step-playlist" GET "$path"
    else
      req "$step-segment" GET "$path"
      return 0
    fi
  done
}

relative() {
  case $2 in
    http://* | https://*) echo "/${2#*://*/}" ;;
    /*) echo "$2" ;;
    *) echo "$1/$2" ;;
  esac
}

is_hls() {
  header "$HEADERS" Content-Type | grep -qi mpegurl
}

web_universal() {
  local cap=$1 query
  query="UserId=$USER_ID&DeviceId=$(auth_of web | sed 's/.*DeviceId="\([^"]*\)".*/\1/')&MaxStreamingBitrate=$cap"
  query+="&Container=$(uri 'opus,webm|opus,ts|mp3,mp3,aac,m4a|aac,m4b|aac,flac,webma,webm|webma,wav,ogg')"
  query+="&TranscodingContainer=ts&TranscodingProtocol=hls&AudioCodec=aac&ApiKey=$TOKEN&PlaySessionId=$SESSION"
  query+="&StartTimeTicks=0&EnableRedirection=true&EnableRemoteMedia=false&EnableAudioVbrEncoding=true"
  local path="/Audio/$ID/universal?$query"
  # jellyfin-web probes the URL with a HEAD that carries no Authorization header.
  req universal-head HEAD "$path"
  if is_hls; then
    req universal-hls GET "$path"
    follow_hls hls "$path"
    return
  fi
  req universal GET "$path" -H 'Range: bytes=0-'
  local total
  total=$(total_of "$HEADERS")
  [ -n "$total" ] && [ "$total" -gt 1 ] && req universal-seek GET "$path" -H "Range: bytes=$((total / 2))-"
  return 0
}

playbackinfo() {
  local profile
  profile=$(cat <<'EOF'
{"DeviceProfile":{"MaxStreamingBitrate":140000000,"MaxStaticBitrate":140000000,"MusicStreamingTranscodingBitrate":384000,
 "DirectPlayProfiles":[{"Container":"opus","Type":"Audio"},{"Container":"webm","AudioCodec":"opus","Type":"Audio"},
  {"Container":"mp3","Type":"Audio"},{"Container":"ts","AudioCodec":"mp3","Type":"Audio"},{"Container":"aac","Type":"Audio"},
  {"Container":"m4a","AudioCodec":"aac","Type":"Audio"},{"Container":"m4b","AudioCodec":"aac","Type":"Audio"},
  {"Container":"flac","Type":"Audio"},{"Container":"webma","Type":"Audio"},{"Container":"wav","Type":"Audio"},{"Container":"ogg","Type":"Audio"}],
 "TranscodingProfiles":[{"Container":"ts","Type":"Audio","AudioCodec":"aac","Context":"Streaming","Protocol":"hls","MaxAudioChannels":"2","MinSegments":1,"BreakOnNonKeyFrames":true,"EnableAudioVbrEncoding":true},
  {"Container":"aac","Type":"Audio","AudioCodec":"aac","Context":"Streaming","Protocol":"http","MaxAudioChannels":"2"},
  {"Container":"mp3","Type":"Audio","AudioCodec":"mp3","Context":"Static","Protocol":"http","MaxAudioChannels":"2"}],
 "ContainerProfiles":[],"CodecProfiles":[],"SubtitleProfiles":[]}}
EOF
)
  req playbackinfo POST "/Items/$ID/PlaybackInfo?UserId=$USER_ID&StartTimeTicks=0&IsPlayback=true&AutoOpenLiveStream=true&MaxStreamingBitrate=140000000" \
    -H "$AUTH" -H 'Accept: application/json' -H 'Content-Type: application/json' -d "$profile"
  jq -r '(.MediaSources // .mediaSources // [])[] | "  source \(.Container // .container): direct play \(.SupportsDirectPlay // .supportsDirectPlay), direct stream \(.SupportsDirectStream // .supportsDirectStream), transcoding \((.TranscodingUrl // .transcodingUrl // "-") | sub("ApiKey=[^&]*"; "ApiKey=..."))"' \
    "$BODY" >&2 2>/dev/null || true
}

# pdf.js: the first GET is dropped once the headers say ranges are fine.
pdf() {
  local path="/Items/$ID/Download?ApiKey=$TOKEN" total chunk=65536
  req pdf-open GET "$path" --max-filesize 1
  total=$(header "$HEADERS" Content-Length)
  if [ "$(header "$HEADERS" Accept-Ranges)" != bytes ] || [ -z "$total" ] || [ "$total" -le $((2 * chunk)) ]; then
    req pdf-whole GET "$path"
    return
  fi
  req pdf-chunk-first GET "$path" -H "Range: bytes=0-$((chunk - 1))"
  req pdf-chunk-last GET "$path" -H "Range: bytes=$(((total - 1) / chunk * chunk))-$((total - 1))"
}

printf 'client\tstep\tmethod\tstatus\ttype\tlength\trange\taccept-ranges\tbytes\tsha256\tfile\n'
case $COMMAND in
  web-audio | web-audio-hls | playbackinfo | lyrics | download | epub | comic | pdf | photo-web) login web ;;
  finamp-file | finamp-hls) login finamp ;;
  jellybook) login jellybook ;;
  photo-tv) login tv ;;
  stream) login legacy ;;
  *) usage ;;
esac
resolve

case $COMMAND in
  web-audio) web_universal 140000000 ;;
  web-audio-hls) web_universal 64000 ;;
  finamp-file)
    req file GET "/Items/$ID/File?ApiKey=$TOKEN"
    total=$(header "$HEADERS" Content-Length)
    [ -z "$total" ] || [ "$total" -le 1 ] || req file-seek GET "/Items/$ID/File?ApiKey=$TOKEN" -H "Range: bytes=$((total / 2))-"
    ;;
  finamp-hls)
    path="/Audio/$ID/main.m3u8?audioCodec=aac&playSessionId=$SESSION&audioSampleRate=44100&segmentContainer=mp4&audioBitRate=${TRANSCODE_BITRATE:-320000}&ApiKey=$TOKEN"
    req main-m3u8 GET "$path"
    follow_hls hls "$path"
    ;;
  stream)
    req stream GET "/Audio/$ID/stream?static=true" -H "$AUTH"
    total=$(header "$HEADERS" Content-Length)
    [ -z "$total" ] || [ "$total" -le 1 ] || req stream-seek GET "/Audio/$ID/stream?static=true" -H "$AUTH" -H "Range: bytes=$((total / 2))-"
    ;;
  playbackinfo) playbackinfo ;;
  lyrics) req lyrics GET "/Audio/$ID/Lyrics" -H "$AUTH" -H 'Accept: application/json' ;;
  download | epub | comic) req "$COMMAND" GET "/Items/$ID/Download?ApiKey=$TOKEN" ;;
  pdf) pdf ;;
  jellybook) req download GET "/Items/$ID/Download" -H "$AUTH" ;;
  photo-web)
    req grid GET "/Items/$ID/Images/Primary?fillHeight=236&fillWidth=354&quality=96&tag=$TAG"
    req slideshow-download GET "/Items/$ID/Download?ApiKey=$TOKEN"
    req slideshow-image GET "/Items/$ID/Images/Primary?quality=100&tag=$TAG"
    ;;
  photo-tv) req image GET "/Items/$ID/Images/Primary?tag=$TAG&maxWidth=1920&maxHeight=1080" ;;
esac
exit $FAILED
