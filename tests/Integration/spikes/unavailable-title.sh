#!/usr/bin/env bash
#
# Phase 0, point 6: how is a title presented when its remote is offline and there is
# no backup source, so that playback is refused with a clear message?
#
# Private Jellyfin 12.4 with the probe, driven through the real jellyfin-web in
# headless Chromium. The .strm files point at an endpoint in the probe itself, which
# stands in for the JellyMesh play endpoint and answers live, 503 or never.
#
# Usage: unavailable-title.sh up|web [case prefix...]|longrun|api|down|all
# Env: P6 (workspace root). Run build-probe.sh first. Results go to stdout, screenshots
# to $P6/run/shots. web_cases.json lists the cases, web_client_refuse.py drives the browser.

set -euo pipefail

P6=${P6:?set P6 to the workspace root}
SPIKES=$P6/out/spikes
RUN=$P6/run
PORT=18260
NAME=jm-e3-p6
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
PLAYWRIGHT=mcr.microsoft.com/playwright/python:v1.48.0-noble
BASE=http://127.0.0.1:$PORT
ACCEPT='Accept: application/json; profile="PascalCase"'
DEVICE=jellymesh-p6-spike
PASSWORD=jellymesh-test
TOKEN=

auth() {
  local header="MediaBrowser Client=\"JellyMeshSpike\", Device=\"p6\", DeviceId=\"$DEVICE\", Version=\"0.1\""
  [ -n "$TOKEN" ] && header+=", Token=\"$TOKEN\""
  printf 'Authorization: %s' "$header"
}
get() { curl -fsS -H "$ACCEPT" -H "$(auth)" "$BASE$1"; }
post() { curl -fsS -X POST -H "$ACCEPT" -H "$(auth)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"; }
# Status and body, for calls expected to fail.
raw() { # <token or -> <method> <path> [body]
  local t=$1 m=$2 p=$3 b=${4:-}
  TOKEN=${t#-} curl -sS -o "$RUN/body" -w '%{http_code}' -X "$m" -H "$ACCEPT" -H "$(TOKEN=${t#-} auth)" \
    -H 'Content-Type: application/json' ${b:+-d "$b"} "$BASE$p"
}
login() { # <user>
  TOKEN=
  TOKEN=$(post /Users/AuthenticateByName "$(jq -n --arg u "$1" --arg p "$PASSWORD" '{Username:$u,Pw:$p}')" | jq -r .AccessToken)
}
mode() { # <refuse> [item]
  curl -fsS -G -X POST --data-urlencode "refuse=$1" --data-urlencode "item=${2:-}" "$BASE/JellyMesh/ProbeMode"
}

strm() { # <dir under fed> <file name> <slug>
  mkdir -p "$RUN/fed/$1"
  echo "http://127.0.0.1:8096/JellyMesh/Play/$3" >"$RUN/fed/$1/$2.strm"
}

fixtures() {
  rm -rf "${RUN:?}/fed"
  strm "Film/JM Offline (2026)" "JM Offline (2026)" film-offline
  strm "Film/JM Online (2026)" "JM Online (2026)" film-online
  local title
  for title in Offline Online; do
    cat >"$RUN/fed/Film/JM $title (2026)/movie.nfo" <<XML
<?xml version="1.0" encoding="utf-8" standalone="yes"?>
<movie><title>JM $title</title><year>2026</year><plot>Trama del film.</plot><lockdata>true</lockdata></movie>
XML
  done
  local middle="Serie/JM Mezzo (2026)/Season 01" head="Serie/JM Testa (2026)/Season 01"
  strm "$middle" "JM Mezzo S01E01" mezzo-1
  strm "$middle" "JM Mezzo S01E02 Offline" mezzo-2-offline
  strm "$middle" "JM Mezzo S01E03" mezzo-3
  strm "$head" "JM Testa S01E01 Offline" testa-1-offline
  strm "$head" "JM Testa S01E02" testa-2
}

add_library() { # <name> <type> <path>
  post "/Library/VirtualFolders?name=$1&collectionType=$2&refreshLibrary=false&paths=$3" \
    '{"LibraryOptions":{"EnableRealtimeMonitor":false}}'
}
scan() {
  local last new i
  last=$(get /ScheduledTasks | jq -r '.[] | select(.Key=="RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
  post /Library/Refresh
  for i in $(seq 1 120); do
    new=$(get /ScheduledTasks | jq -r '.[] | select(.Key=="RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && return
    sleep 1
  done
  echo "scan did not finish" >&2; return 1
}

add_user() { # <name> <jq policy edit>
  local id policy
  id=$(post /Users/New "$(jq -n --arg n "$1" --arg p "$PASSWORD" '{Name:$n,Password:$p}')" | jq -r .Id)
  policy=$(get "/Users/$id" | jq -c ".Policy | $2")
  post "/Users/$id/Policy" "$policy"
}

up() {
  down
  mkdir -p "$RUN/shots"
  fixtures
  docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" \
    -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$RUN/fed:/federata:ro" \
    "$IMAGE" >/dev/null
  local i
  for i in $(seq 1 120); do get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && break; sleep 1; done
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  get /Startup/User >/dev/null
  post /Startup/User "$(jq -n --arg p "$PASSWORD" '{Name:"admin",Password:$p}')"
  post /Startup/Complete
  docker exec "$NAME" /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -loglevel error -y \
    -f lavfi -i testsrc2=s=640x360:r=24:d=8 -f lavfi -i sine=d=8 \
    -c:v libvpx-vp9 -b:v 300k -deadline realtime -cpu-used 8 -c:a libopus -b:a 64k /config/clip.webm
  docker exec "$NAME" mkdir -p /config/plugins/JellyMeshProbe_0.0.1.0
  docker cp "$SPIKES/JellyMeshProbe/bin-out/JellyMeshProbe.dll" "$NAME:/config/plugins/JellyMeshProbe_0.0.1.0/"
  docker cp "$SPIKES/JellyMeshProbe/bin-out/meta.json" "$NAME:/config/plugins/JellyMeshProbe_0.0.1.0/"
  docker restart "$NAME" >/dev/null
  sleep 3
  for i in $(seq 1 120); do get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && break; sleep 1; done
  login admin
  add_library Film movies /federata/Film
  add_library Serie tvshows /federata/Serie
  scan
  add_user p6user '.'
  add_user p6noaccess '.EnableAllFolders=false | .EnabledFolders=[]'
  add_user p6noplay '.EnableMediaPlayback=false'
  get "/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields=Path" | jq -r '.Items[] | "\(.Type) \(.Name) \(.IndexNumber // "-")"'
}

item_id() { # <jq select on Name>
  get "/Items?Recursive=true&IncludeItemTypes=Movie,Series,Episode&Fields=Path" | jq -r ".Items[] | select($1) | .Id" | head -1
}

config() { # writes $RUN/web.json for the browser
  login p6user
  local user server offline online testa mezzo season episodes
  user=$(get /Users/Me | jq -r .Id); server=$(get /System/Info/Public | jq -r .Id)
  offline=$(item_id '.Name=="JM Offline"'); online=$(item_id '.Name=="JM Online"')
  testa=$(item_id '.Name=="JM Testa" and .Type=="Series"'); mezzo=$(item_id '.Name=="JM Mezzo" and .Type=="Series"')
  season=$(get "/Shows/$mezzo/Seasons" | jq -r '.Items[0].Id')
  episodes=$(get "/Items?Recursive=true&IncludeItemTypes=Episode" | jq -c '[.Items[].Id]')
  local film
  film=$(get /UserViews | jq -r '.Items[] | select(.Name=="Film") | .Id')
  jq -n --arg base http://127.0.0.1:8096 --arg token "$TOKEN" --arg user "$user" --arg server "$server" \
    --arg device "$DEVICE" --arg offline "$offline" --arg online "$online" --arg testa "$testa" \
    --arg mezzo "$mezzo" --arg season "$season" --arg film "$film" --argjson episodes "$episodes" --slurpfile cases "$SPIKES/web_cases.json" \
    '{base:$base,token:$token,user:$user,server:$server,device:$device,
      items:{offline:$offline,online:$online,testa:$testa,mezzo:$mezzo,season:$season,film:$film},
      episodes:$episodes,cases:$cases[0]}' \
    | sed -e "s/@FILM@/$film/g" -e "s/@SEASON@/$season/g" >"$RUN/web.json"
}

web() { # [case prefix...]
  config
  docker run --rm --network "container:$NAME" -v "$SPIKES:/spike:ro" -v "$RUN:/run-out" "$PLAYWRIGHT" sh -c \
    "pip install -q --no-warn-script-location playwright==1.48.0 2>/dev/null; python3 -I /spike/web_client_refuse.py /run-out/web.json /run-out/shots $*" 2>&1 |
    grep -v '^$' | sed -E 's/[0-9a-f]{32}/<hex>/g'
}

api() {
  source "$SPIKES/unavailable-title-api.sh"
}

# Gives every stub a two hour length, as the plugin would, so watch state rules apply.
longrun() {
  login admin
  docker exec "$NAME" sh -c "printf longrun >/config/jmprobe.mode"
  post "/Items/$(item_id '.Name=="JM Offline"')/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default&replaceAllMetadata=false"
  sleep 4
  get "/Items/$(item_id '.Name=="JM Offline"')" | jq -c '{Name,RunTimeTicks}'
}

down() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "$NAME-config" "$NAME-cache" >/dev/null 2>&1 || true
}

case ${1:-} in
  up) up ;;
  web) shift; web "$@" ;;
  api) api ;;
  longrun) longrun ;;
  down) down ;;
  all)
    up; web
    # A second pass with a real length: watched state depends on it.
    up; longrun; web status-503 code-NotAllowed notrans-file guard-hls late-hls-dead after-file-503
    docker exec "$NAME" rm -f /config/jmprobe.mode
    api; down ;;
  *) echo "usage: $0 up|web [case...]|longrun|api|down|all" >&2; exit 2 ;;
esac
