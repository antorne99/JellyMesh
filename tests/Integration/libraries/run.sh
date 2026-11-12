#!/usr/bin/env bash
# Integration 7 of the per-server spec: the libraries JellyMesh keeps for a server (D6, D7, D16, D20, decisions 131 and 140).
# Starts a private instance with the plugin and a stock remote, checks, then removes both with their volumes.
# usage: run.sh   (PREFIX and PORT pick the private containers, KEEP=1 leaves them running)
set -euo pipefail

PREFIX=${PREFIX:-jm-libs}
PORT=${PORT:-18520}
IMAGE=${IMAGE:-jellyfin/jellyfin:12.4.20261026-211555}
LOCAL=$PREFIX-local
REMOTE=$PREFIX-remote
NET=$PREFIX-net
WT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
BASE=http://127.0.0.1:$PORT
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshLibs", Device="libs", DeviceId="jm-libs-e2e", Version="0.1"'
TOKEN=
PASSED=0
FAILED=0

auth_header() {
  if [ -n "$TOKEN" ]; then printf 'Authorization: %s, Token="%s"' "$AUTH" "$TOKEN"
  else printf 'Authorization: %s' "$AUTH"; fi
}
get() { curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }
post() { curl -fsS -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"; }
# Posts and prints the status code, then the body on the next line.
post_status() {
  local body
  body=$(mktemp)
  curl -sS -o "$body" -w '%{http_code}\n' -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"
  cat "$body"
  rm -f "$body"
}
delete() { curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }
urlencode() { jq -rn --arg v "$1" '$v|@uri'; }

check() { # <description> <command...>
  local what=$1
  shift
  if "$@"; then
    PASSED=$((PASSED + 1)); echo "  ok   $what"
  else
    FAILED=$((FAILED + 1)); echo "  FAIL $what"
  fi
}

wait_ready() {
  local i
  for i in $(seq 1 120); do
    get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return
    sleep 1
  done
  echo "$BASE not ready" >&2; return 1
}

login() { # <user> <password>
  TOKEN=
  TOKEN=$(post /Users/AuthenticateByName "$(jq -n --arg u "$1" --arg p "$2" '{Username:$u,Pw:$p}')" | jq -r .AccessToken)
}

wizard() {
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  get /Startup/User >/dev/null
  post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}'
  post /Startup/Complete
}

cleanup() {
  [ "${KEEP:-0}" = 1 ] && return
  docker rm -f "$LOCAL" "$REMOTE" >/dev/null 2>&1 || true
  docker volume rm "$LOCAL-config" "$LOCAL-cache" "$LOCAL-fed" "$REMOTE-config" "$REMOTE-cache" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

folders() { get /Library/VirtualFolders; }
folder() { folders | jq --arg n "$1" '.[] | select(.Name == $n)'; }
state() { get /JellyMesh/Admin/Status | jq --arg id "$SERVER" '.Servers[] | select(.Id == $id) | .AutomaticLibraries'; }
registry() { state | jq -c '[.Libraries[].Name]'; }
released() { state | jq -c '.Released'; }
exists() { folders | jq -e --arg n "$1" 'any(.[]; .Name == $n)' >/dev/null; }
missing() { ! exists "$1"; }
plugin_active() { get /Plugins | jq -e 'any(.[]; .Name == "JellyMesh" and .Status == "Active")' >/dev/null; }
count_under() { (get "/Items?ParentId=$1&Recursive=true" 2>/dev/null || echo '{}') | jq -r '.TotalRecordCount // 0'; }

wait_libraries() {
  local i
  for i in $(seq 1 300); do
    [ "$(state | jq -r .Busy)" = false ] && return
    sleep 1
  done
  echo "the libraries job did not finish" >&2; return 1
}

sync_full() {
  local i
  post /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 180); do
    [ "$(get /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return
    sleep 1
  done
  echo "sync did not finish" >&2; return 1
}

scan_idle() {
  local i
  for i in $(seq 1 300); do
    [ "$(get /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .State')" = Idle ] && return
    sleep 1
  done
  echo "scan did not end" >&2; return 1
}

movies_in() { get "/Items?ParentId=$1&Recursive=true&IncludeItemTypes=Movie" | jq .TotalRecordCount; }

safe_options() { # <library name>
  folder "$1" | jq -e '.LibraryOptions
    | .MetadataSavers == [] and .TypeOptions == [] and .SaveLocalMetadata == false and .EnableRealtimeMonitor == false
      and .EnableAutomaticSeriesGrouping == true and .EnableChapterImageExtraction == false
      and .EnableTrickplayImageExtraction == false and .EnableLUFSScan == false and .AutomaticallyAddToCollection == false' >/dev/null
}

only_path() { # <library name> <path> <type>
  folder "$1" | jq -e --arg p "$2" --arg t "$3" '.Locations == [$p] and .CollectionType == $t' >/dev/null
}

echo "== setup: $LOCAL (port $PORT) and $REMOTE"
cleanup
docker network create "$NET" >/dev/null
docker run -d --name "$REMOTE" --network "$NET" -e TZ=Europe/Rome \
  -v "$REMOTE-config:/config" -v "$REMOTE-cache:/cache" "$IMAGE" >/dev/null
docker run -d --name "$LOCAL" --network "$NET" -p "127.0.0.1:$PORT:8096" -e TZ=Europe/Rome -e JELLYMESH_FEDERATED_ROOT=/federata \
  -v "$LOCAL-config:/config" -v "$LOCAL-cache:/cache" -v "$LOCAL-fed:/federata" "$IMAGE" >/dev/null

# The remote only needs a non-admin user to log in with.
for i in $(seq 1 120); do
  docker exec "$REMOTE" curl -fsS http://127.0.0.1:8096/System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$REMOTE" sh -c '
  h="Accept: application/json; profile=\"PascalCase\""
  a="Authorization: MediaBrowser Client=\"x\", Device=\"x\", DeviceId=\"x\", Version=\"0.1\""
  b=http://127.0.0.1:8096
  curl -fsS -X POST -H "$h" -H "$a" -H "Content-Type: application/json" -d "{\"UICulture\":\"en-US\"}" $b/Startup/Configuration
  curl -fsS -H "$h" -H "$a" $b/Startup/User >/dev/null
  curl -fsS -X POST -H "$h" -H "$a" -H "Content-Type: application/json" -d "{\"Name\":\"admin\",\"Password\":\"jellymesh-test\"}" $b/Startup/User
  curl -fsS -X POST -H "$h" -H "$a" $b/Startup/Complete
  t=$(curl -fsS -X POST -H "$h" -H "$a" -H "Content-Type: application/json" -d "{\"Username\":\"admin\",\"Pw\":\"jellymesh-test\"}" $b/Users/AuthenticateByName | sed -n "s/.*\"AccessToken\":\"\([^\"]*\)\".*/\1/p")
  curl -fsS -X POST -H "$h" -H "$a, Token=\"$t\"" -H "Content-Type: application/json" -d "{\"Name\":\"jellymesh\",\"Password\":\"jellymesh-test\"}" $b/Users/New >/dev/null
'

wait_ready
wizard
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$LOCAL"
sleep 3
wait_ready
login admin jellymesh-test
check "plugin loaded" plugin_active

SERVER=$(post /JellyMesh/Admin/Servers "$(jq -n --arg u "http://$REMOTE:8096" '{Name:"NAS: Casa",Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" | jq -r .Id)
GUID8=$(echo "$SERVER" | tr -d - | cut -c1-8)
FOLDER=$(echo "$SERVER" | tr -d - | sed -E 's/^(.{8})(.{4})(.{4})(.{4})(.{12})$/\1-\2-\3-\4-\5/')
FILM_PATH=/federata/$FOLDER/Film
SERIE_PATH=/federata/$FOLDER/Serie
echo "   server $SERVER"
check "switch off by default" [ "$(state | jq -r .Enabled)" = false ]

# A film in the server folder shows that the libraries are scanned after their creation.
docker exec "$LOCAL" sh -c "mkdir -p '$FILM_PATH/Dummy (2020)' /other && /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i testsrc=duration=1:size=320x240:rate=10 -c:v mpeg4 -y '$FILM_PATH/Dummy (2020)/Dummy (2020).mkv'"
# D16: the sanitized Film name is already taken by an admin library.
post "/Library/VirtualFolders?name=$(urlencode 'NAS  Casa - Film')&collectionType=movies&refreshLibrary=false&paths=/other" '{"LibraryOptions":{}}'

echo "== switch on [D6, D16, D20, name with :]"
code=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":true}' | head -1)
check "switching on answers 202" [ "$code" = 202 ]
wait_libraries
FILM="NAS  Casa $GUID8 - Film"
SERIE="NAS  Casa - Serie"
echo "   libraries: $(folders | jq -c '[.[].Name]')"
check "taken Film name gets the GUID digits" only_path "$FILM" "$FILM_PATH" movies
check "Serie created with ':' made a space" only_path "$SERIE" "$SERIE_PATH" tvshows
check "the admin library is untouched" only_path "NAS  Casa - Film" /other movies
check "Film options are the safe ones" safe_options "$FILM"
check "Serie options are the safe ones" safe_options "$SERIE"
check "registry holds both names" [ "$(registry)" = "$(jq -nc --arg f "$FILM" --arg s "$SERIE" '[$f,$s]')" ]
scan_idle
FILM_ID=$(folder "$FILM" | jq -r .ItemId)
check "the film is in the new Film library after the scan" [ "$(movies_in "$FILM_ID")" = 1 ]

echo "== options after a restart and after an admin save"
docker restart "$LOCAL" >/dev/null
sleep 3
wait_ready
login admin jellymesh-test
check "MetadataSavers still empty after restart" safe_options "$FILM"
post /Library/VirtualFolders/LibraryOptions "$(folder "$SERIE" | jq '{Id: .ItemId, LibraryOptions: .LibraryOptions}')"
check "MetadataSavers still empty after an admin save" safe_options "$SERIE"

echo "== dashboard rename and path change [D7]"
post "/Library/VirtualFolders/Name?name=$(urlencode "$SERIE")&newName=$(urlencode 'Serie di Casa')&refreshLibrary=false"
sleep 2
scan_idle
sync_full
check "a rename is followed" [ "$(registry)" = "$(jq -nc --arg f "$FILM" '[$f,"Serie di Casa"]')" ]
NEW_SERIE_ID=$(folder 'Serie di Casa' | jq -r .ItemId)
docker exec "$LOCAL" mkdir -p /other2
post "/Library/VirtualFolders/Paths?refreshLibrary=false" "$(jq -n --arg n "$FILM" '{Name:$n,PathInfo:{Path:"/other2"}}')"
sync_full
check "a library with a new path leaves the registry" [ "$(registry)" = '["Serie di Casa"]' ]
check "and is listed as left to the admin" [ "$(released)" = "$(jq -nc --arg f "$FILM" '[$f]')" ]

echo "== switch off with a confirmation [D6]"
answer=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":false}')
check "switching off without a confirmation answers 409" [ "$(echo "$answer" | head -1)" = 409 ]
check "and names only the registry library" [ "$(echo "$answer" | tail -n +2 | jq -c .ToDelete)" = '["Serie di Casa"]' ]
check "nothing is deleted before the confirmation" exists 'Serie di Casa'
code=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":false,"Confirmed":true}' | head -1)
check "the confirmed switch off answers 202" [ "$code" = 202 ]
wait_libraries
echo "   libraries: $(folders | jq -c '[.[].Name]')"
check "the registry library is deleted" missing 'Serie di Casa'
check "the library left to the admin stays" exists "$FILM"
check "the admin library stays" only_path "NAS  Casa - Film" /other movies
check "its items are gone" [ "$(count_under "$NEW_SERIE_ID")" = 0 ]
check "the switch is off" [ "$(state | jq -r .Enabled)" = false ]

echo "== switch on again while a scan runs"
delete "/Library/VirtualFolders?name=$(urlencode "$FILM")&refreshLibrary=false"
post /Library/Refresh
code=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":true}' | head -1)
check "switching on during a scan answers 202" [ "$code" = 202 ]
wait_libraries
scan_idle
FILM_ID=$(folder "$FILM" | jq -r .ItemId)
check "Film created again" only_path "$FILM" "$FILM_PATH" movies
check "the film is in it once the scans ended" [ "$(movies_in "$FILM_ID")" = 1 ]
check "the old notices are cleared" [ "$(state | jq -c .Released)" = '[]' ]

held_none() { ! docker exec "$LOCAL" sh -c 'grep -qs "<RemovedServerLibrariesConfig>" /config/plugins/configurations/*.xml'; }

wait_missing() { # <library name>
  local i
  for i in $(seq 1 300); do
    missing "$1" && return
    sleep 1
  done
  echo "$1 was not deleted" >&2; return 1
}

echo "== an admin library on the server folder before the switch-on is never taken [131]"
code=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":false,"Confirmed":true}' | head -1)
check "the confirmed switch off answers 202" [ "$code" = 202 ]
wait_libraries
post "/Library/VirtualFolders?name=$(urlencode 'Mine on the folder')&collectionType=movies&refreshLibrary=false&paths=$(urlencode "$FILM_PATH")" '{"LibraryOptions":{}}'
code=$(post_status "/JellyMesh/Admin/Servers/$SERVER/AutomaticLibraries" '{"Enabled":true}' | head -1)
check "switching on answers 202" [ "$code" = 202 ]
wait_libraries
scan_idle
check "Film is created next to the admin library" only_path "$FILM" "$FILM_PATH" movies
delete "/Library/VirtualFolders?name=$(urlencode "$FILM")&refreshLibrary=false"
scan_idle
sync_full
check "the admin library is not taken for the deleted Film" [ "$(registry)" = "$(jq -nc --arg s "$SERIE" '[$s]')" ]
check "the deleted Film is listed as gone" [ "$(released)" = "$(jq -nc --arg f "$FILM" '[$f]')" ]

echo "== removing the server deletes its libraries with no confirmation [140]"
delete "/JellyMesh/Admin/Servers/$SERVER"
wait_missing "$SERIE"
echo "   libraries: $(folders | jq -c '[.[].Name]')"
check "the registry library is deleted" missing "$SERIE"
check "the admin library on the server folder stays" only_path 'Mine on the folder' "$FILM_PATH" movies
check "the admin library elsewhere stays" only_path "NAS  Casa - Film" /other movies
check "nothing is left to delete" held_none

echo "passed $PASSED, failed $FAILED"
[ "$FAILED" = 0 ]
