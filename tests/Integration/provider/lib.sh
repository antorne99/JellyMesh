# Helpers for the Phase 7 proof: two private JellyMesh servers federated to each other. Source it.
#   a  jm-prov-a  18410  media of remoto-standard  consumer of b and of the stock remote
#   b  jm-prov-b  18411  media of remoto-jellymesh consumer of a
# The servers reach each other through gate.py on the host, so that a test can slow them down or cut them off:
#   18412 -> a, 18413 -> b, 18414 -> stock remote (8097)
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
MEDIA=${MEDIA:?point MEDIA at tests/Integration/media of the main checkout}
WORK=${WORK:?point WORK at a scratch folder}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshProv", Device="prov", DeviceId="jm-prov-admin", Version="0.1"'
HOST_GATE=host.docker.internal
INCREMENTAL_TASK="JellyMesh incremental sync"
RECONCILE_TASK="JellyMesh reconciliation"
LIMIT_TEXT="Limite di stream raggiunto sul server di origine. Riprova più tardi."
OFFLINE_TEXT="Server non raggiungibile"
FAILED_TEXT="Riproduzione non disponibile al momento. Riprova più tardi."
PROFILE_DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
PROFILE_HLS='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
mkdir -p "$WORK"

port_of() { case $1 in a) echo 18410 ;; b) echo 18411 ;; esac; }
container_of() { echo "jm-prov-$1"; }
token_of() { cat "$WORK/token-$1" 2>/dev/null || true; }

# req <a|b> <method> <path> [body]: prints the body; the status goes to $WORK/status.
req() {
  local s=$1 method=$2 path=$3 body=${4:-} token
  token=$(token_of "$s")
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" \
    -H "Authorization: $AUTH, Token=\"$token\"" -H 'Content-Type: application/json' \
    ${body:+-d "$body"} "http://127.0.0.1:$(port_of "$s")$path" >"$WORK/status"
  cat "$WORK/body"
}
status() { cat "$WORK/status"; }
get() { req "$1" GET "$2"; }
post() { req "$1" POST "$2" "${3:-}"; }

# as_peer <a|b> <user> <method> <path>: the same request with the login of a dedicated user. Prints the status.
peer_token() {
  curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe-$2\", Version=\"0.1\"" \
    -H 'Content-Type: application/json' -d "{\"Username\":\"$2\",\"Pw\":\"jellymesh-test\"}" \
    "http://127.0.0.1:$(port_of "$1")/Users/AuthenticateByName" | jq -r .AccessToken
}
as_peer() {
  local s=$1 token=$2 method=$3 path=$4
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"peer\", DeviceId=\"jm-prov-probe\", Version=\"0.1\", Token=\"$token\"" \
    "http://127.0.0.1:$(port_of "$s")$path"
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
  post "$1" /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken >"$WORK/token-$1.new"
  mv "$WORK/token-$1.new" "$WORK/token-$1"
}

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

# start <a|b> <instance whose media it mounts>
start() {
  local s=$1 instance=$2 name
  name=$(container_of "$s")
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1 || true
  rm -f "$WORK/token-$s"
  docker run -d --name "$name" -p "127.0.0.1:$(port_of "$s"):8096" --add-host host.docker.internal:host-gateway \
    -e TZ=Europe/Rome \
    -v "$name-config:/config" -v "$name-cache:/cache" -v "$name-fed:/federata" -v "$name-extra:/extra" \
    -v "$MEDIA/$instance:/media:ro" "$IMAGE" >/dev/null
  wait_ready "$s"
  post "$s" /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null
  get "$s" /Startup/User >/dev/null
  post "$s" /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
  post "$s" /Startup/Complete >/dev/null
  docker exec "$name" sh -c 'mkdir -p /federata/Film /federata/Serie /extra/film && touch /federata/Film/.jellymesh /federata/Serie/.jellymesh /extra/film/.keep'
  login "$s"
  local opts='{"LibraryOptions":{"EnableInternetProviders":false}}'
  post "$s" "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film&paths=/extra/film&paths=/federata/Film" "$opts" >/dev/null
  post "$s" "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/media/serie&paths=/federata/Serie" "$opts" >/dev/null
  post "$s" /Users/New '{"Name":"peer","Password":"jellymesh-test"}' >/dev/null
  post "$s" /Users/New '{"Name":"mario","Password":"jellymesh-test"}' >/dev/null
  scan "$s"
}

install_plugin() {
  NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$(container_of "$1")"
  sleep 3
  wait_ready "$1"
  login "$1"
}

user_id() { get "$1" /Users | jq -r --arg n "$2" '.[] | select(.Name == $n) | .Id'; }
library_id() { get "$1" /JellyMesh/Admin/Peers/Options | jq -r --arg n "$2" '.Libraries[] | select(.Name == $n) | .Id'; }

# share <a|b> <limit> <library name>...: what the dedicated user may have on that server.
share() {
  local s=$1 limit=$2 uid ids=()
  shift 2
  uid=$(user_id "$s" peer)
  for name in "$@"; do ids+=("$(library_id "$s" "$name")"); done
  req "$s" PUT "/JellyMesh/Admin/Peers/$uid" "$(printf '%s\n' "${ids[@]:-}" | jq -R . | jq -s --argjson m "$limit" '{SharedLibraryIds: map(select(length > 0)), MaxStreams: $m}')" >/dev/null
  [ "$(status)" = 204 ] || { echo "share failed: $(status)" >&2; return 1; }
}

# add_remote <consumer> <name> <url> <user> -> server id. Asks the gate-side URL, which is what the consumer really uses.
add_remote() {
  post "$1" /JellyMesh/Admin/Servers "$(jq -n --arg n "$2" --arg u "$3" --arg x "$4" '{Name:$n,Url:$u,Username:$x,Password:"jellymesh-test"}')" | jq -r .Id
}

# import_all <consumer> <server id>: every library the remote offers.
import_all() {
  local ids
  ids=$(get "$1" "/JellyMesh/Admin/Servers/$2/Libraries" | jq -c '{LibraryIds: map(.Id)}')
  post "$1" "/JellyMesh/Admin/Servers/$2/Libraries" "$ids" >/dev/null
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

# run_task <s> <name>: runs a scheduled task and waits for it to end.
run_task() {
  local s=$1 name=$2 id last new i
  id=$(get "$s" /ScheduledTasks | jq -r --arg n "$name" '.[] | select(.Name == $n) | .Id')
  last=$(get "$s" "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post "$s" "/ScheduledTasks/Running/$id" >/dev/null
  for i in $(seq 1 180); do
    new=$(get "$s" "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && return 0
    sleep 1
  done
  echo "task $name did not end" >&2
  return 1
}

gate_mode() { echo "$2" >"$WORK/gate-$1"; }

# Clients of b: each device logs in by itself, because a session belongs to a device.
client_login() {
  curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"$1\", DeviceId=\"$1\", Version=\"0.1\"" \
    -H 'Content-Type: application/json' -d '{"Username":"admin","Pw":"jellymesh-test"}' \
    http://127.0.0.1:18411/Users/AuthenticateByName | jq -r .AccessToken >"$WORK/client-$1"
}
# open_stream <device> <item> <profile>: PlaybackInfo on b. Prints the status; the answer is in $WORK/open-<device>.
open_stream() {
  local device=$1 item=$2 profile=$3
  [ -f "$WORK/client-$device" ] || client_login "$device"
  curl -sS -o "$WORK/open-$device" -w '%{http_code}' -X POST -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"$device\", DeviceId=\"$device\", Version=\"0.1\", Token=\"$(cat "$WORK/client-$device")\"" \
    -H 'Content-Type: application/json' -d "$profile" "http://127.0.0.1:18411/Items/$item/PlaybackInfo"
}
# close_stream <device>: what a client does at the end of a playback.
close_stream() {
  local device=$1 session
  session=$(jq -r '.PlaySessionId // empty' "$WORK/open-$device" 2>/dev/null)
  [ -n "$session" ] || return 0
  curl -sS -o /dev/null -X DELETE -H "Authorization: MediaBrowser Client=\"JellyMeshProv\", Device=\"$device\", DeviceId=\"$device\", Version=\"0.1\", Token=\"$(cat "$WORK/client-$device")\"" \
    "http://127.0.0.1:18411/Videos/ActiveEncodings?deviceId=$device&playSessionId=$session"
}
stream_url() { echo "http://127.0.0.1:18411$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/open-$1")"; }
active_streams() { get a /JellyMesh/Admin/Peers | jq -r '.[0].ActiveStreams'; }
folder_of() { local plain=${1%%\?*}; echo "${plain%/*}/"; }
# hls_segments <device> <how many>: walks an HLS stream as a player does, each URI against the playlist it came from. Prints
# the status of each segment. What the client was given stays in $WORK/seen-*, for leaks.sh.
hls_segments() {
  local device=$1 count=$2 url variant segment
  url=$(stream_url "$device")
  curl -sS "$url" >"$WORK/seen-$device-master"
  variant=$(folder_of "$url")$(grep -v '^#' "$WORK/seen-$device-master" | head -1)
  curl -sS "$variant" >"$WORK/seen-$device-variant"
  for segment in $(grep -v '^#' "$WORK/seen-$device-variant" | head -"$count"); do
    curl -sS -o /dev/null -w '%{http_code}\n' --max-time 60 "$(folder_of "$variant")$segment"
  done
}
# movie_on_b <title>: the stub of a title on b.
movie_on_b() { get b '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$1" '[.Items[] | select((.Path // "") | contains($t))][0].Id'; }
# message_during <device> <seconds> <command...>: runs the command with the device connected, prints what it was told.
message_during() {
  local device=$1 seconds=$2
  shift 2
  [ -f "$WORK/client-$device" ] || client_login "$device"
  python3 "$S/ws.py" 18411 "$(cat "$WORK/client-$device")" "$device" "$seconds" >"$WORK/ws-$device" &
  local listener=$!
  sleep 2
  "$@" >"$WORK/during-$device"
  wait "$listener"
  sed -n 's/^DisplayMessage: //p' "$WORK/ws-$device" | head -1
}

check() { # check <what> <expected> <actual>
  if [ "$2" = "$3" ]; then echo "PASS $1: $3"; else echo "FAIL $1: expected $2, got $3"; FAILED=1; fi
}
