# Helpers for the proof of tasks 504, 505 and 602. Source it.
#   local   jm-play-local   18430  plugin, writable /media, consumer of remote
#   remote  jm-play-remote  18431  stock Jellyfin with the media of remoto-standard
#   peer    jm-play-peer    18432  plugin, consumer of local: proves the rules on federated items
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
MEDIA=${MEDIA:?point MEDIA at tests/Integration/media of the main checkout}
WORK=${WORK:-${TMPDIR:-/tmp}/jm-play-run}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
PW_IMAGE=mcr.microsoft.com/playwright/python:v1.48.0-noble
PLUGIN=28c43ea1-fb5a-4b3a-8e6e-30aa3245c653
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshPlay", Device="play", DeviceId="jm-play-admin", Version="0.1"'
PROFILE_HLS='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
mkdir -p "$WORK/web"

port_of() { case $1 in local) echo 18430 ;; remote) echo 18431 ;; peer) echo 18432 ;; esac; }
name_of() { echo "jm-play-$1"; }
token_of() { cat "$WORK/token-$1" 2>/dev/null || true; }

# req <server> <method> <path> [body]: prints the body; the status goes to $WORK/status.
req() {
  local s=$1 method=$2 path=$3 body=${4:-}
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" \
    -H "Authorization: $AUTH, Token=\"$(token_of "$s")\"" -H 'Content-Type: application/json' \
    ${body:+-d "$body"} "http://127.0.0.1:$(port_of "$s")$path" >"$WORK/status"
  cat "$WORK/body"
}
status() { cat "$WORK/status"; }
get() { req "$1" GET "$2"; }
post() { req "$1" POST "$2" "${3:-}"; }

FAILED=0
check() { # check <what> <expected> <got>
  if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected [$2], got [$3]"; FAILED=1; fi
}

wait_ready() {
  local i
  for i in $(seq 1 120); do
    curl -fsS "http://127.0.0.1:$(port_of "$1")/System/Info/Public" 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "server $1 is not ready" >&2
  return 1
}

login() {
  rm -f "$WORK/token-$1"
  post "$1" /Users/AuthenticateByName "{\"Username\":\"${2:-admin}\",\"Pw\":\"jellymesh-test\"}" | jq -r .AccessToken >"$WORK/token-$1.new"
  mv "$WORK/token-$1.new" "$WORK/token-$1"
}

# start <server> <media mount>: START_ENV adds environment options, such as the variable of an older version.
start() {
  local s=$1 mount=$2 name
  name=$(name_of "$s")
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$name-config" "$name-cache" "$name-fed" >/dev/null 2>&1 || true
  rm -f "$WORK/token-$s"
  docker run -d --name "$name" -p "127.0.0.1:$(port_of "$s"):8096" --add-host host.docker.internal:host-gateway \
    -e TZ=Europe/Rome ${START_ENV:-} \
    -v "$name-config:/config" -v "$name-cache:/cache" -v "$name-fed:/federata" -v "$mount" "$IMAGE" >/dev/null
  wait_ready "$s"
  post "$s" /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null
  get "$s" /Startup/User >/dev/null
  post "$s" /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
  post "$s" /Startup/Complete >/dev/null
  docker exec "$name" sh -c 'mkdir -p /federata/Film /federata/Serie && touch /federata/Film/.jellymesh /federata/Serie/.jellymesh'
  login "$s"
}

library() { # library <server> <name> <type> <path>...
  local s=$1 name=$2 type=$3 query="" p
  shift 3
  for p in "$@"; do query="$query&paths=$p"; done
  post "$s" "/Library/VirtualFolders?name=$name&collectionType=$type&refreshLibrary=false$query" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
}

user() { post "$1" /Users/New "{\"Name\":\"$2\",\"Password\":\"jellymesh-test\"}" | jq -r .Id; }
user_id() { get "$1" /Users | jq -r --arg n "$2" '.[] | select(.Name == $n) | .Id'; }

scan() {
  local s=$1 last new i
  last=$(get "$s" /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
  post "$s" /Library/Refresh >/dev/null
  for i in $(seq 1 300); do
    new=$(get "$s" /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && { sleep 2; return 0; }
    sleep 1
  done
  echo "scan of $s did not end" >&2
  return 1
}

sync_now() {
  local s=$1 i
  post "$s" /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 180); do
    [ "$(get "$s" /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return 0
    sleep 1
  done
  echo "sync of $s did not end" >&2
  return 1
}

settings() { # settings <server> <jq filter>: changes the plugin settings
  local c
  c=$(get "$1" "/Plugins/$PLUGIN/Configuration" | jq -c "$2")
  post "$1" "/Plugins/$PLUGIN/Configuration" "$c" >/dev/null
  status
}

add_remote() { # add_remote <consumer> <name> <url> <user> -> server id
  post "$1" /JellyMesh/Admin/Servers "$(jq -n --arg n "$2" --arg u "$3" --arg x "$4" '{Name:$n,Url:$u,Username:$x,Password:"jellymesh-test"}')" | jq -r .Id
}

import_all() { # import_all <consumer> <server id>
  local ids
  ids=$(get "$1" "/JellyMesh/Admin/Servers/$2/Libraries" | jq -c '{LibraryIds: map(.Id)}')
  post "$1" "/JellyMesh/Admin/Servers/$2/Libraries" "$ids" >/dev/null
}

# item <server> <title substring> [item type]: the id of an item whose path has that substring and is a stub or not.
stub_item() { get "$1" '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$2" '[.Items[] | select((.Path // "") | (contains($t) and startswith("/federata")))][0].Id // ""'; }
local_item() { get "$1" '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$2" '[.Items[] | select((.Path // "") | (contains($t) and startswith("/media")))][0].Id // ""'; }
stub_url() { get "$1" "/Items?Ids=$2&Fields=MediaSources" | jq -r '.Items[0].MediaSources[0].Path'; }

strm_file() { docker exec "$(name_of "$1")" sh -c "find /federata/Film -name '*.strm' | grep -F \"$2\" | head -1"; }
strm_text() { docker exec "$(name_of "$1")" sh -c "cat \"\$(find /federata/Film -name '*.strm' | grep -F \"$2\" | head -1)\""; }

# viewer_login: a login for the viewer on the device of the browser, or its messages would go elsewhere.
viewer_login() {
  curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -d '{"Username":"viewer","Pw":"jellymesh-test"}' \
    -H 'Authorization: MediaBrowser Client="Jellyfin Web", Device="Chromium", DeviceId="jm-play-browser", Version="12.4.0"' \
    http://127.0.0.1:18430/Users/AuthenticateByName | jq -r .AccessToken >"$WORK/token-viewer"
}
as_viewer() { # as_viewer <method> <path> [body]: prints the status; the answer is in $WORK/viewer-body
  curl -sS -o "$WORK/viewer-body" -w '%{http_code}' -X "$1" -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"viewer\", DeviceId=\"jm-play-viewer\", Version=\"0.1\", Token=\"$(cat "$WORK/token-viewer")\"" \
    -H 'Content-Type: application/json' ${3:+-d "$3"} "http://127.0.0.1:18430$2"
}

# web <item> <scenario> <capture> [seconds]: real jellyfin-web in Chromium, as the viewer.
web() {
  local item=$1 scenario=$2 cap=$3 secs=${4:-12} me sid
  me=$(as_viewer GET /Users/Me >/dev/null; jq -r .Id "$WORK/viewer-body")
  sid=$(curl -fsS http://127.0.0.1:18430/System/Info/Public | jq -r .Id)
  mkdir -p "$WORK/web"
  cp "$WT/tests/Integration/playback/webplay.py" "$WORK/web/webplay.py"
  docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v jm-play-pw:/pw -e PYTHONPATH=/pw "$PW_IMAGE" \
    bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
    /w/webplay.py http://host.docker.internal:18430 "$(cat "$WORK/token-viewer")" "$me" "$sid" "$item" "$scenario" "/w/$cap" "$secs"
}

down() {
  docker rm -f jm-play-local jm-play-remote jm-play-peer >/dev/null 2>&1 || true
  docker volume rm jm-play-local-config jm-play-local-cache jm-play-local-fed \
    jm-play-remote-config jm-play-remote-cache jm-play-remote-fed \
    jm-play-peer-config jm-play-peer-cache jm-play-peer-fed jm-play-media jm-play-pw >/dev/null 2>&1 || true
}
