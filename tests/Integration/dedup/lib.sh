# Helpers for the proof of decisions 13 and 13a, on private servers. Source it.
#   stock    jm-dedup-s  18422  media of remoto-standard   no plugin
#   peer     jm-dedup-j  18423  media of remoto-jellymesh  plugin, shares its libraries
#   fresh    jm-dedup-f  18421  media of locale            plugin, consumer of both, from an empty folder
#   upgrade  jm-dedup-u  18424  media of locale            plugin, consumer of both, first with the old build
# They share the network jm-dedup-net. Never touches the jellymesh-test stack.
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
MEDIA=${MEDIA:-$WT/tests/Integration/media}
WORK=${WORK:-${TMPDIR:-/tmp}/jm-dedup-work}
IMAGE=$(awk '/image:/ {print $2; exit}' "$WT/tests/Integration/docker-compose.yml")
NETWORK=jm-dedup-net
PLUGIN=28c43ea1-fb5a-4b3a-8e6e-30aa3245c653
TEST_KEY=jm-dedup-test-key-0123456789abcdef0123456789
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshDedup", Device="dedup", DeviceId="jm-dedup-admin", Version="0.1"'
DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[]}}'
mkdir -p "$WORK"

port_of() { case $1 in fresh) echo 18421 ;; stock) echo 18422 ;; peer) echo 18423 ;; upgrade) echo 18424 ;; esac; }
container_of() { case $1 in fresh) echo jm-dedup-f ;; stock) echo jm-dedup-s ;; peer) echo jm-dedup-j ;; upgrade) echo jm-dedup-u ;; esac; }
media_of() { case $1 in fresh | upgrade) echo locale ;; stock) echo remoto-standard ;; peer) echo remoto-jellymesh ;; esac; }
token_of() { cat "$WORK/token-$1" 2>/dev/null || true; }

req() { # req <server> <method> <path> [body]: prints the body; the status goes to $WORK/status.
  local s=$1 method=$2 path=$3 body=${4:-}
  curl -sS -o "$WORK/body-$s" -w '%{http_code}' -X "$method" -H "$ACCEPT" \
    -H "Authorization: $AUTH, Token=\"$(token_of "$s")\"" -H 'Content-Type: application/json' \
    ${body:+-d "$body"} "http://127.0.0.1:$(port_of "$s")$path" >"$WORK/status"
  cat "$WORK/body-$s"
}
get() { req "$1" GET "$2"; }
post() { req "$1" POST "$2" "${3:-}"; }

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

scan_state() { get "$1" /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | "\(.State) \(.LastExecutionResult.EndTimeUtc // "")"'; }

scan() {
  local s=$1 last i
  last=$(scan_state "$s" | cut -d' ' -f2)
  post "$s" /Library/Refresh >/dev/null
  for i in $(seq 1 300); do
    case $(scan_state "$s") in
      Idle\ "$last") ;;
      Idle\ *) sleep 2; return 0 ;;
    esac
    sleep 1
  done
  echo "scan of $s did not end" >&2
  return 1
}

# Waits for a scan the plugin queued, if any.
settle() {
  local s=$1 i
  sleep 3
  for i in $(seq 1 180); do
    [ "$(scan_state "$s" | cut -d' ' -f1)" = Idle ] && { sleep 2; return 0; }
    sleep 1
  done
  echo "scan of $s did not settle" >&2
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

sync_and_scan() { sync_now "$1"; settle "$1"; scan "$1"; settle "$1"; }

# run_container <server> [key]: the container of a server over its volumes. An empty key leaves the variable unset.
run_container() {
  local s=$1 key=${2-$TEST_KEY} name
  name=$(container_of "$s")
  docker run -d --name "$name" --network "$NETWORK" -p "127.0.0.1:$(port_of "$s"):8096" \
    -e TZ=Europe/Rome ${key:+-e JELLYMESH_SECRET_KEY=$key} \
    -v "$name-config:/config" -v "$name-cache:/cache" -v "$name-fed:/federata" -v "$name-extra:/extra" \
    -v "$MEDIA/$(media_of "$s"):/media:ro" "$IMAGE" >/dev/null
  wait_ready "$s"
}

start() {
  local s=$1 name opts
  name=$(container_of "$s")
  docker rm -f "$name" >/dev/null 2>&1 || true
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1 || true
  rm -f "$WORK/token-$s"
  run_container "$s"
  post "$s" /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null
  get "$s" /Startup/User >/dev/null
  post "$s" /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
  post "$s" /Startup/Complete >/dev/null
  docker exec "$name" sh -c 'mkdir -p /federata/Film /federata/Serie /extra/film /extra/serie && touch /federata/Film/.jellymesh /federata/Serie/.jellymesh /extra/film/.keep /extra/serie/.keep && chmod -R a+rwX /extra'
  login "$s"
  opts='{"LibraryOptions":{"EnableInternetProviders":false}}'
  post "$s" "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film&paths=/extra/film&paths=/federata/Film" "$opts" >/dev/null
  post "$s" "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=false&paths=/media/serie&paths=/extra/serie&paths=/federata/Serie" "$opts" >/dev/null
  post "$s" /Users/New '{"Name":"jellymesh","Password":"jellymesh-test"}' >/dev/null
  scan "$s"
}

# install_plugin <server> [repo]: the current build, or the one of another checkout.
install_plugin() {
  NUGET_VOLUME=jellymesh-nuget bash "${2:-$WT}/tests/Integration/scripts/install-plugin.sh" "$(container_of "$1")" >/dev/null
  sleep 3
  wait_ready "$1"
  login "$1"
}

configure() { # configure <server> <metadata protection>
  local cfg
  cfg=$(get "$1" "/Plugins/$PLUGIN/Configuration" | jq --argjson p "$2" '.FederatedFolderPath = "/federata" | .MetadataProtection = $p')
  post "$1" "/Plugins/$PLUGIN/Configuration" "$cfg" >/dev/null
}

add_remote() { # add_remote <consumer> <name> <url> -> server id
  local id ids
  id=$(post "$1" /JellyMesh/Admin/Servers "$(jq -n --arg n "$2" --arg u "$3" '{Name:$n,Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" | jq -r .Id)
  ids=$(get "$1" "/JellyMesh/Admin/Servers/$id/Libraries" | jq -c '{LibraryIds: map(.Id)}')
  post "$1" "/JellyMesh/Admin/Servers/$id/Libraries" "$ids" >/dev/null
  echo "$id"
}

share_all() { # share_all <server>: everything to the dedicated user
  local uid ids
  uid=$(get "$1" /Users | jq -r '.[] | select(.Name == "jellymesh") | .Id')
  ids=$(get "$1" /JellyMesh/Admin/Peers/Options | jq -c '{SharedLibraryIds: [.Libraries[].Id], MaxStreams: 4}')
  req "$1" PUT "/JellyMesh/Admin/Peers/$uid" "$ids" >/dev/null
  [ "$(cat "$WORK/status")" = 204 ] || { echo "share failed: $(cat "$WORK/status")" >&2; return 1; }
}

admin_id() { get "$1" /Users/Me | jq -r .Id; }
items() { # items <server> <type>
  get "$1" "/Items?userId=$(admin_id "$1")&Recursive=true&IncludeItemTypes=$2&Fields=Path,Overview&Limit=500"
}
stubs() { docker exec "$(container_of "$1")" sh -c 'cd /federata && find . -type f -name "*.strm" | sort'; }

# Episodes of JM Serie Doppia, as "S1E3-4@stub" or "S1E1-2@local": what a client sees.
doppia() {
  items "$1" Episode | jq -c '[.Items[] | select(.SeriesName == "JM Serie Doppia")
    | "E\(.IndexNumber)\(if .IndexNumberEnd then "-\(.IndexNumberEnd)" else "" end)@\(if (.Path | startswith("/federata")) then "stub" else "local" end)"] | sort_by(ltrimstr("E") | split("-")[0] | split("@")[0] | tonumber)'
}
episode_id() { # episode_id <server> <path suffix>
  items "$1" Episode | jq -r --arg p "$2" '.Items[] | select(.Path | endswith($p)) | .Id' | head -1
}
film_rows() { # film_rows <server> <title>: how many films of that exact name
  items "$1" Movie | jq --arg n "$2" '[.Items[] | select(.Name == $n)] | length'
}
content() { # content <server> <key>: the decision of a key, shortened
  get "$1" /JellyMesh/Admin/Index/Decisions | jq -c --arg k "$2" '[.Contents[] | select(.Identity.Key == $k)
    | {local: .IsLocal, winner: (.Winner.ServerName // null), height: (.Winner.Quality.Height // null), backups: [.Backups[].ServerName]}] | first'
}

check() { # check <what> <expected> <actual>
  if [ "$2" = "$3" ]; then echo "PASS $1: $3"; else echo "FAIL $1: expected $2, got $3"; FAILED=1; fi
}
