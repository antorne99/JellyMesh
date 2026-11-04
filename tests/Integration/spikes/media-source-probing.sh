#!/usr/bin/env bash
#
# Phase 0, point 3: what stops the local ffprobe when a .strm is played, and what
# does the client receive? One probe plugin, with its hooks switched by a mode file.
#
# Needs P3 set to the workspace root and a built probe (build-probe.sh). Private
# containers only, ports 18220-18223. Everything goes to stdout, so "all" can be
# redirected to a transcript. Fixtures are locked, as the plan requires (§3.1, §10).
#
# Usage: media-source-probing.sh all | up | down
#        media-source-probing.sh scenario <label> <modes> [unlocked]
#
# Modes (see JellyMeshProbe/Catalog.cs):
#   source     IMediaSourceProvider adds a prefilled source
#   sameid     ...reusing the item Id
#   noprobe    pre-refresh provider clears EnableRemoteContentProbe
#   prefill    pre-refresh provider writes runtime, bitrate, container and streams
#   external   the catalog also lists 2 external subtitles (remote Index layout)
#   resave     a provider after ProbeProvider saves the streams again
#   decorator  IMediaSourceManager wrapper forces allowMediaProbe=false (restart needed)
#   rewrite    ...and replaces Path
#   chapters   a provider after ProbeProvider saves 2 chapters
#   epnumbers  pre-refresh provider fills season and episode numbers when missing
#   filter     MVC filter answers PlaybackInfo itself
#   filter-after, filter-ds, filter-file  MVC filter edits the controller's answer

set -euo pipefail

source "$(dirname "$0")/media-source-env.sh"

LOCK='<lockdata>true</lockdata>'
WEB_PORT=18223
WEB_LOG=$P3/run/stub-web.log
FIELDS='{Id,Name,Protocol,Path,Container,Size,Bitrate,RunTimeTicks,IsRemote,SupportsProbing,SupportsDirectPlay,SupportsDirectStream,SupportsTranscoding,TranscodingUrl,DefaultAudioStreamIndex}'

summary() { # <PlaybackInfo json on stdin>
  sed -E 's/ApiKey=[0-9a-f]+/ApiKey=<token>/g; s/(TranscodingUrl":"[^?]*)\?[^"]*/\1?.../' |
    jq -c ".MediaSources[] | $FIELDS + {Streams: [.MediaStreams[] | \"\(.Index):\(.Type[0:1]):\(.Codec)\(if .IsExternal then \":ext\" else \"\" end)\"]}"
}

persisted() { # <id>
  get "/Items?Ids=$1&Fields=MediaSources" | jq -c '.Items[0] | {RunTimeTicks, Container, Path: .MediaSources[0].Path, Streams: [.MediaSources[0].MediaStreams[] | "\(.Index):\(.Type[0:1]):\(.Codec)\(if .IsExternal then ":ext" else "" end)"]}'
}

stub_since() { tail -n +$(($1 + 1)) "$STUB_LOG" | sed 's/^/  stub: /'; }

timed_playback_info() { # <id> <profile> <output>
  /usr/bin/time -p curl -fsS -o "$3" -X POST -H "$ACCEPT" -H "$(auth_header)" \
    -H 'Content-Type: application/json' -d "$(jq -n --argjson p "$2" '{DeviceProfile:$p}')" \
    "$BASE/Items/$1/PlaybackInfo" 2>&1 | awk '/real/{print "PlaybackInfo took " $2 "s"}'
}

measure() { # <instance> <label> <id>
  local s0 p0 i
  s0=$(stub_lines); p0=$(ffprobe_on_stub "$1")
  for i in 1 2; do timed_playback_info "$3" "$PROFILE_ALL" "$P3/run/$2-all-$i.json"; done
  playback_info "$3" "$PROFILE_WEB" >"$P3/run/$2-web.json"
  sleep 1
  echo "3 PlaybackInfo: stub requests $(($(stub_lines) - s0)), ffprobe runs $(($(ffprobe_on_stub "$1") - p0))"
  echo "all-direct profile:"; summary <"$P3/run/$2-all-2.json"
  echo "web-like profile:"; summary <"$P3/run/$2-web.json"
  echo "persisted:"; persisted "$3"
}

scenario() { # <label> <modes> [unlocked]
  local label=$1 modes=$2 lock=$LOCK title id
  [ "${3:-}" = unlocked ] && lock=
  title="JM S $label"
  use probe; login
  # The decorator is chosen at startup and stays for the life of the process, so
  # every scenario restarts: a previous decorator must not leak into this one.
  set_mode probe "$modes"
  restart probe
  fixture probe "$title" "s-$label" "$lock"
  scan_and_wait
  id=$(item_id "$title")
  echo; echo "== $label modes=[$modes] lock=${lock:+on}"
  echo "after scan:"; persisted "$id"
  measure probe "$label" "$id"
}

baseline() {
  use plain; login
  fixture plain "JM Baseline" baseline "$LOCK"
  scan_and_wait
  local id; id=$(item_id "JM Baseline")
  echo; echo "== baseline, no plugin, lock=on"
  echo "after scan:"; persisted "$id"
  measure plain baseline "$id"
}

# What the server does once a stub has data and no probe: stream, transcode,
# subtitles, reporting. Run with no decorator in the process.
playback_checks() {
  local title="JM S checks" id s0 p0 url playlist segment user
  use probe; login
  set_mode probe noprobe,prefill,external,resave
  restart probe
  fixture probe "$title" s-checks "$LOCK"
  scan_and_wait
  id=$(item_id "$title")
  echo; echo "== checks on a prefilled locked stub, remote Index layout"

  s0=$(stub_lines)
  curl -s -o /dev/null -w 'static stream: %{http_code}\n' -H "$(auth_header)" -H 'Range: bytes=0-99' \
    "$BASE/Videos/$id/stream?static=true&mediaSourceId=$id"
  sleep 1; stub_since "$s0"

  playback_info "$id" "$PROFILE_WEB" >"$P3/run/checks.json"
  url=$(jq -r '.MediaSources[0].TranscodingUrl' "$P3/run/checks.json")
  echo "transcode, AudioStreamIndex in URL: $(grep -oE 'AudioStreamIndex=[0-9]+' <<<"$url")"
  s0=$(stub_lines)
  playlist=$(curl -s -H "$(auth_header)" "$BASE$url" | grep -v '^#' | head -1)
  segment=$(curl -s -H "$(auth_header)" "$BASE/videos/$id/$playlist" | grep -v '^#' | head -1)
  curl -s -o /dev/null -w 'segment: %{http_code} %{size_download}\n' -H "$(auth_header)" "$BASE/videos/$id/$segment"
  sleep 2; stub_since "$s0"
  docker exec "$(name_of probe)" sh -c 'head -3 "$(ls -t /config/log/FFmpeg* | head -1)" | tail -1' |
    grep -oE '\-i "[^"]+"|-map [^ ]+' | tr '\n' ' '; echo

  jq -c '.MediaSources[0].MediaStreams[] | select(.Type == "Subtitle") | {Index, IsExternal, DeliveryMethod, Path}' "$P3/run/checks.json"
  s0=$(stub_lines); p0=$(ffprobe_on_stub probe)
  curl -s -o /dev/null -w 'embedded subtitle via local endpoint: %{http_code}\n' -H "$(auth_header)" \
    "$BASE/Videos/$id/$id/Subtitles/5/0/Stream.vtt"
  sleep 2; stub_since "$s0"; echo "ffprobe runs $(($(ffprobe_on_stub probe) - p0))"

  s0=$(stub_lines); p0=$(ffprobe_on_stub probe)
  post /Sessions/Playing "$(jq -n --arg i "$id" '{ItemId:$i,MediaSourceId:$i,PositionTicks:0,PlayMethod:"Transcode",PlaySessionId:"p3"}')"
  post /Sessions/Playing/Stopped "$(jq -n --arg i "$id" '{ItemId:$i,MediaSourceId:$i,PositionTicks:600000000,PlaySessionId:"p3"}')"
  sleep 1
  user=$(get /Users/Me | jq -r .Id)
  echo "reporting: stub requests $(($(stub_lines) - s0)), ffprobe runs $(($(ffprobe_on_stub probe) - p0)), played: $(get "/Users/$user/Items/$id" | jq -c '.UserData | {PlaybackPositionTicks, Played}')"

  echo "local file on the same server, source untouched:"
  id=$(item_id "Sintel")
  playback_info "$id" "$PROFILE_ALL" | jq -c '.MediaSources[] | {Path, Protocol, RunTimeTicks, Streams: (.MediaStreams | length)}'
}

persistence() { # <label> <modes> [unlocked]
  local label=$1 modes=$2 lock=$LOCK title id p0
  [ "${3:-}" = unlocked ] && lock=
  title="JM S $label"
  use probe; login
  set_mode probe "$modes"
  restart probe
  fixture probe "$title" "s-$label" "$lock"
  scan_and_wait
  id=$(item_id "$title")
  echo; echo "== persistence $label modes=[$modes] lock=${lock:+on}"
  persisted "$id"
  p0=$(ffprobe_on_stub probe)
  echo "scan:"; scan_and_wait; persisted "$id"
  echo "full refresh:"; post "/Items/$id/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh"; sleep 3; persisted "$id"
  echo "replace all:"; post "/Items/$id/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true&ReplaceAllImages=true"; sleep 3; persisted "$id"
  echo "ffprobe runs $(($(ffprobe_on_stub probe) - p0))"
}

# Same refreshes with the hooks switched off, to see what the server does to the data.
no_hooks() {
  local title="JM S nohooks" id
  use probe; login
  set_mode probe noprobe,prefill,external,resave
  restart probe
  fixture probe "$title" s-nohooks "$LOCK"
  scan_and_wait
  id=$(item_id "$title")
  set_mode probe off
  echo; echo "== data written once, then hooks off"
  persisted "$id"
  echo "scan:"; scan_and_wait; persisted "$id"
  echo "replace all:"; post "/Items/$id/Refresh?MetadataRefreshMode=FullRefresh&ImageRefreshMode=FullRefresh&ReplaceAllMetadata=true"; sleep 3; persisted "$id"
}

episode_strm() { # <show dir> <number>
  local dir=$1/"Season 01" name="JM Serie Stub S01E0$2"
  mkdir -p "$dir"
  echo "$STUB/JellyMesh/Play/ep$2" >"$dir/$name.strm"
  cat >"$dir/$name.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<episodedetails><title>Stub $2</title><season>1</season><episode>$2</episode><lockdata>true</lockdata></episodedetails>
XML
}

# Episodes: one created with the series, one added later (it inherits IsLocked).
episodes() {
  local show="$P3/run/federata-probe/Serie/JM Serie Stub (2026)" user show_id
  use probe; login
  set_mode probe noprobe,prefill,external,resave,chapters
  restart probe
  mkdir -p "$show"
  cat >"$show/tvshow.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<tvshow><title>JM Serie Stub</title><year>2026</year><uniqueid type="tvdb" default="true">999000301</uniqueid><lockdata>true</lockdata></tvshow>
XML
  episode_strm "$show" 1
  scan_and_wait
  episode_strm "$show" 2
  scan_and_wait
  user=$(get /Users/Me | jq -r .Id)
  echo; echo "== episodes, locked series, E02 added by a later scan"
  get "/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path" |
    jq -r '.Items[] | select(.Path | startswith("/federata/")) | .Id' | while read -r id; do
      get "/Users/$user/Items/$id" | jq -c '{Name, IndexNumber, ParentIndexNumber, LockData, RunTimeTicks}'
      measure probe "ep-$id" "$id" | grep -E 'stub requests|^persisted' -A1 | grep -v -e '^--' -e '^all-direct' | cut -c1-200
    done
  set_mode probe noprobe,prefill,external,resave,chapters,epnumbers
  episode_strm "$show" 3
  scan_and_wait
  echo "E03 added by a later scan, with the plugin filling numbers:"
  show_id=$(get "/Items?Recursive=true&IncludeItemTypes=Series" | jq -r '.Items[] | select(.Name == "JM Serie Stub") | .Id')
  get "/Shows/$show_id/Episodes?UserId=$user&Fields=Path" | jq -c '.Items[] | {Name, IndexNumber, ParentIndexNumber, SeasonName}'
}

chapters_check() {
  local title="JM S chapters" id
  use probe; login
  set_mode probe noprobe,prefill,chapters
  restart probe
  fixture probe "$title" s-chapters "$LOCK"
  scan_and_wait
  id=$(item_id "$title")
  echo; echo "== chapters on a locked stub"
  get "/Items?Ids=$id&Fields=Chapters" | jq -c '.Items[0].Chapters | map(.Name)'
  playback_info "$id" "$PROFILE_ALL" >/dev/null
  echo "after PlaybackInfo, provider on:"; get "/Items?Ids=$id&Fields=Chapters" | jq -c '.Items[0].Chapters | map(.Name)'
  set_mode probe noprobe,prefill
  playback_info "$id" "$PROFILE_ALL" >/dev/null
  echo "after PlaybackInfo, provider off:"; get "/Items?Ids=$id&Fields=Chapters" | jq -c '.Items[0].Chapters | map(.Name)'
}

# Path rewritten by the decorator: does the server follow it?
rewrite_checks() {
  local id s0 url playlist segment
  use probe; login
  id=$(item_id "JM S decrewrite")
  echo; echo "== server playback with Path rewritten by the decorator"
  s0=$(stub_lines)
  curl -s -o /dev/null -w 'static stream: %{http_code}\n' -H "$(auth_header)" -H 'Range: bytes=0-99' "$BASE/Videos/$id/stream?static=true&mediaSourceId=$id"
  sleep 1; stub_since "$s0"
  url=$(playback_info "$id" "$PROFILE_WEB" | jq -r '.MediaSources[0].TranscodingUrl')
  s0=$(stub_lines)
  playlist=$(curl -s -H "$(auth_header)" "$BASE$url" | grep -v '^#' | head -1)
  segment=$(curl -s -H "$(auth_header)" "$BASE/videos/$id/$playlist" | grep -v '^#' | head -1)
  curl -s -o /dev/null -w 'segment: %{http_code}\n' -H "$(auth_header)" "$BASE/videos/$id/$segment"
  sleep 2; stub_since "$s0"
}

# The filter answers before the controller, so it must enforce library access.
filter_access() {
  local id blocked token=
  use probe; login
  set_mode probe filter
  id=$(item_id "JM S filt")
  echo; echo "== filter, user without access to the library"
  blocked=$(post /Users/New '{"Name":"jmblocked","Password":"jellymesh-test"}' | jq -r .Id)
  get "/Users/$blocked" | jq -c '.Policy | .EnableAllFolders = false | .EnabledFolders = []' >"$P3/run/policy.json"
  post "/Users/$blocked/Policy" "$(cat "$P3/run/policy.json")"
  login jmblocked jellymesh-test
  curl -s -o /dev/null -w 'PlaybackInfo as blocked user: %{http_code}\n' -X POST -H "$(auth_header)" \
    -H 'Content-Type: application/json' -d '{}' "$BASE/Items/$id/PlaybackInfo"
  curl -s -o /dev/null -w 'PlaybackInfo with no token: %{http_code}\n' -X POST -H 'Content-Type: application/json' -d '{}' "$BASE/Items/$id/PlaybackInfo"
}

web_case() { # <instance> <title> <log> <seconds>
  local s0 id; s0=$(wc -l <"$3"); id=$(item_id "$2")
  webplay "$1" "$id" "$4" | grep -E '^(codecs|REQ|RES|video src)' | cut -c1-170 | sort -u -k1,3 || true
  echo "stub:"; sleep 1; tail -n +$((s0 + 1)) "$3" | cut -c1-150 | sed 's/^/  /'
}

web_fixture() { # <instance> <title> <slug>
  local dir="$P3/run/federata-$1/Film/$2 (2026)"
  mkdir -p "$dir"
  echo "http://host.docker.internal:$WEB_PORT/JellyMesh/Play/$3" >"$dir/$2 (2026).strm"
  printf '<?xml version="1.0" encoding="utf-8" standalone="yes"?>\n<movie><title>%s</title><year>2026</year>%s</movie>\n' "$2" "$LOCK" >"$dir/movie.nfo"
}

# Real jellyfin-web in headless Chromium, which has VP9 but no H.264 here, so a
# webm stands in for "a file this browser can play". At most 2 containers up.
web() {
  docker stop "$(name_of probe)" >/dev/null
  use plain; wait_ready; login
  web_fixture plain "JM Web Diretto" web
  scan_and_wait
  echo; echo "== web client, no plugin, playable container"
  web_case plain "JM Web Diretto" "$WEB_LOG" 8
  echo; echo "== web client, no plugin, mkv/h264 not playable here"
  web_case plain "JM Baseline" "$STUB_LOG" 8
  docker stop "$(name_of plain)" >/dev/null

  docker start "$(name_of probe)" >/dev/null
  use probe; wait_ready; login
  web_fixture probe "JM Web Proxy" proxy
  set_mode probe filter-after,filter-ds
  restart probe
  scan_and_wait
  echo; echo "== web client, filter sets DirectPlay false and DirectStream true"
  web_case probe "JM Web Proxy" "$WEB_LOG" 8
  set_mode probe filter
  echo; echo "== web client, filter answers PlaybackInfo, TranscodingUrl on a plugin path (no endpoint: 404 expected)"
  web_case probe "JM Web Proxy" "$WEB_LOG" 6
  web_file
  docker start "$(name_of plain)" >/dev/null
}

web_file() {
  use probe; login
  set_mode probe filter-after,filter-file
  echo; echo "== web client, filter reports Protocol File and IsRemote false"
  web_case probe "JM Web Proxy" "$WEB_LOG" 8
}

up() {
  mkdir -p "$P3/run"
  : >"$STUB_LOG"; : >"$WEB_LOG"
  nohup python3 "$REPO/spikes/stub_server.py" "$STUB_PORT" "$CLIP" "$STUB_LOG" >/dev/null 2>&1 &
  echo $! >"$P3/run/stub.pid"
  start plain; start probe
  docker run --rm -v "$P3/run:/out" --entrypoint /usr/lib/jellyfin-ffmpeg/ffmpeg "$IMAGE" -hide_banner -loglevel error -nostdin -y \
    -f lavfi -i testsrc=size=640x360:rate=24 -f lavfi -i sine=frequency=440 -t 20 \
    -c:v libvpx-vp9 -b:v 300k -deadline realtime -cpu-used 8 -c:a libopus -b:a 64k /out/clip.webm
  nohup python3 "$REPO/spikes/stub_server.py" "$WEB_PORT" "$P3/run/clip.webm" "$WEB_LOG" >/dev/null 2>&1 &
  echo $! >"$P3/run/stub-web.pid"
  local instance
  for instance in plain probe; do
    use "$instance"; wait_ready; wizard; login
    mkdir -p "$P3/run/federata-$instance/Serie"; touch "$P3/run/federata-$instance/Serie/.jellymesh"
    post "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film&paths=/federata/Film" \
      '{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
    debug_probe_log "$instance"
  done
  use probe; login
  post "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/federata/Serie" \
    '{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
  install_probe probe
  restart plain; restart probe
  use plain; login; scan_and_wait
  use probe; login; scan_and_wait
  get /Plugins | jq -c '.[] | select(.Name == "JellyMesh Probe") | {Name, Version, Status}'
}

down() {
  local pid
  for pid in stub stub-web; do
    [ -f "$P3/run/$pid.pid" ] && kill "$(cat "$P3/run/$pid.pid")" 2>/dev/null || true
    rm -f "$P3/run/$pid.pid"
  done
  docker rm -f "$(name_of plain)" "$(name_of probe)" >/dev/null 2>&1 || true
  docker volume rm jm-e3-p3-plain-config jm-e3-p3-plain-cache jm-e3-p3-probe-config jm-e3-p3-probe-cache >/dev/null 2>&1 || true
  rm -rf "$P3"/run/federata-*
}

all() {
  up
  baseline
  scenario source source
  scenario sameid source,sameid
  scenario hooksonly noprobe
  scenario prefillonly prefill
  scenario pre noprobe,prefill
  scenario preresave noprobe,prefill,external,resave
  scenario unlocked noprobe,prefill,external,resave unlocked
  scenario dec decorator,prefill
  scenario decrewrite decorator,prefill,rewrite
  rewrite_checks
  scenario filt filter
  filter_access
  scenario filtafter filter-after,noprobe,prefill
  playback_checks
  persistence persist noprobe,prefill,external,resave
  persistence persistun noprobe,prefill,external,resave unlocked
  no_hooks
  chapters_check
  episodes
  web
}

case ${1:-} in
  all) all ;;
  up) up ;;
  down) down ;;
  scenario) shift; scenario "$@" ;;
  web_file) web_file ;;
  *) echo "usage: $0 all | up | down | scenario <label> <modes> [unlocked]" >&2; exit 2 ;;
esac
