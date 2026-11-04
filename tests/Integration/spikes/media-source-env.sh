#!/usr/bin/env bash
#
# Phase 0, point 3: shared helpers for the media source spike. Source this file.
#
# Starts private Jellyfin instances, never the compose project. Paths come from
# P3 (workspace root). Instance "plain" has no plugin, "probe" gets the probe.

set -euo pipefail

P3=${P3:?set P3 to the workspace root}
REPO=$P3/tests/Integration
source "$REPO/scripts/common.sh"

IMAGE=jellyfin/jellyfin:12.4.20261026-211555
AUTH='MediaBrowser Client="JellyMeshSpike", Device="p3", DeviceId="jellymesh-p3-spike", Version="0.1"'
STUB_PORT=${STUB_PORT:-18221}
STUB=http://host.docker.internal:$STUB_PORT
STUB_LOG=$P3/run/stub.log
# Media stay in the real repo and are only ever mounted read-only.
MEDIA=${MEDIA:-$REPO/media}
CLIP="$MEDIA/remoto-standard/film/JM Audio e Sottotitoli (2026)/JM Audio e Sottotitoli (2026).mkv"

port_of() { case $1 in plain) echo 18220 ;; probe) echo 18222 ;; *) return 1 ;; esac; }
name_of() { echo "jm-e3-p3-$1"; }

use() { BASE=http://127.0.0.1:$(port_of "$1"); TOKEN=; }

start() { # <instance>
  local n; n=$(name_of "$1")
  mkdir -p "$P3/run/federata-$1/Film" "$P3/run/plugins-$1"
  docker run -d --name "$n" -p "127.0.0.1:$(port_of "$1"):8096" \
    --add-host host.docker.internal:host-gateway \
    -v "$n-config:/config" -v "$n-cache:/cache" \
    -v "$P3/run/federata-$1:/federata:ro" \
    -v "$MEDIA/locale:/media:ro" \
    "$IMAGE" >/dev/null
}

wait_ready() {
  local i
  for i in $(seq 1 120); do
    get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return
    sleep 1
  done
  echo "$BASE did not become ready" >&2; return 1
}

wizard() {
  [ "$(get /System/Info/Public | jq -r .StartupWizardCompleted)" = true ] && return
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  get /Startup/User >/dev/null
  post /Startup/User "$(jq -n --arg n "$ADMIN_USER" --arg p "$ADMIN_PASSWORD" '{Name:$n,Password:$p}')"
  post /Startup/Complete
}

scan_task() { get /ScheduledTasks | jq '.[] | select(.Key == "RefreshLibrary")'; }

scan_and_wait() {
  local last new i
  last=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post /Library/Refresh
  for i in $(seq 1 300); do
    new=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && return
    sleep 1
  done
  echo "scan did not finish on $BASE" >&2; return 1
}

# fixture <instance> <title> <stub path> <extra nfo xml>
fixture() {
  local dir="$P3/run/federata-$1/Film/$2 (2026)"
  mkdir -p "$dir"
  echo "$STUB/JellyMesh/Play/$3" >"$dir/$2 (2026).strm"
  cat >"$dir/movie.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie>
  <title>$2</title>
  <year>2026</year>
  <plot>Plot from the NFO.</plot>
  <runtime>2</runtime>
  <uniqueid type="tmdb" default="true">9990003$RANDOM</uniqueid>
  $4
</movie>
XML
}

item_id() { # <title>
  get "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path" | jq -r --arg t "$1" '.Items[] | select(.Name == $t) | .Id'
}

# A browser-like profile: mp4/h264 plays directly, everything else goes to HLS.
PROFILE_WEB='{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Container":"mp4","Type":"Video","VideoCodec":"h264","AudioCodec":"aac"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming","MaxAudioChannels":"2","BreakOnNonKeyFrames":true}],"SubtitleProfiles":[{"Format":"vtt","Method":"External"}]}'
# A native-player-like profile: anything plays directly.
PROFILE_ALL='{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video"}],"TranscodingProfiles":[],"SubtitleProfiles":[{"Format":"srt","Method":"External"},{"Format":"subrip","Method":"Embed"}]}'

playback_info() { # <item id> <profile json> [mediaSourceId]
  local body
  body=$(jq -n --argjson p "$2" --arg m "${3:-}" '{DeviceProfile:$p} + (if $m=="" then {} else {MediaSourceId:$m} end)')
  post "/Items/$1/PlaybackInfo" "$body"
}

# Marks on the server log and stub log, so a window can be cut out afterwards.
stub_lines() { wc -l <"$STUB_LOG" | tr -d ' '; }
server_lines() { docker logs "$(name_of "$1")" 2>&1 | wc -l | tr -d ' '; }

# ffprobe command lines are logged at Debug only.
debug_probe_log() { # <instance>
  docker exec -i "$(name_of "$1")" sh -c 'cat >/config/config/logging.json' <<'JSON'
{"Serilog":{"MinimumLevel":{"Default":"Information","Override":{"Microsoft":"Warning","System":"Warning","MediaBrowser.MediaEncoding.Encoder.MediaEncoder":"Debug"}}}}
JSON
}

install_probe() { # <instance>
  local n; n=$(name_of "$1")
  docker exec "$n" mkdir -p /config/plugins/JellyMeshProbe_0.0.1.0
  docker cp "$P3/out/spikes/JellyMeshProbe/bin-out/JellyMeshProbe.dll" "$n:/config/plugins/JellyMeshProbe_0.0.1.0/"
  docker cp "$P3/out/spikes/JellyMeshProbe/bin-out/meta.json" "$n:/config/plugins/JellyMeshProbe_0.0.1.0/"
}

set_mode() { # <instance> <comma list>
  docker exec "$(name_of "$1")" sh -c "printf '%s' '$2' >/config/jmprobe.mode"
}

restart() { docker restart "$(name_of "$1")" >/dev/null; use "$1"; wait_ready; login; }

file_log() { docker exec "$(name_of "$1")" sh -c 'cat /config/log/log_*.log'; }
ffprobe_on_stub() { # <instance>: ffprobe runs that targeted the stub, ever
  file_log "$1" | grep -c "ffprobe.*host.docker.internal" || true
}

# webplay <instance> <item id> [seconds]: plays in the real jellyfin-web, headless.
# Shares the instance's network, so the page is same-origin and host.docker.internal
# means what the .strm says. The image has the browsers, but not the Python package.
# The sed also masks tokens, which are 32 hex characters.
webplay() {
  local user server token=$TOKEN
  user=$(get /Users/Me | jq -r .Id); server=$(get /System/Info/Public | jq -r .Id)
  docker run --rm --network "container:$(name_of "$1")" -v "$P3/out/spikes:/spike:ro" \
    mcr.microsoft.com/playwright/python:v1.48.0-noble sh -c \
    "pip install -q --no-warn-script-location playwright==1.48.0 2>/dev/null; python3 /spike/web_client_play.py http://127.0.0.1:8096 $token $user $server $2 ${3:-8}" 2>&1 |
    grep -v '^$' | sed -E 's/[0-9a-f]{32}/<hex>/g'
}
