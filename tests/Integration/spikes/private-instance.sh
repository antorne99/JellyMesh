# Helpers for Phase 0 spikes that need their own Jellyfin 12.4: they change
# libraries and run scans, so they can never use the shared instances. Source
# this file, do not run it.
#
# Each scenario starts from a fresh instance (new volumes), so no row, user data
# or cached item carries over from the previous one.
#
# Env: WORK (writable folder for fixtures and results), NAME, PORT, REPO_MEDIA.

set -euo pipefail

IMAGE=jellyfin/jellyfin:12.4.20261026-211555
NAME=${NAME:-jm-spike-merge}
PORT=${PORT:-18210}
WORK=${WORK:?set WORK to a writable folder}
REPO_MEDIA=${REPO_MEDIA:?set REPO_MEDIA to tests/Integration/media/locale}
BASE=http://127.0.0.1:$PORT
FED=$WORK/fed
EXTRA=$WORK/local-extra
RESULTS=$WORK/results
DB_COPY=$WORK/db/$NAME
AUTH='MediaBrowser Client="JellyMeshSpike", Device="spike", DeviceId="jm-spike-p2p7", Version="0.1"'
ACCEPT='Accept: application/json; profile="PascalCase"'
TOKEN=
USER_ID=
mkdir -p "$RESULTS" "$DB_COPY"

auth_header() {
  if [ -n "$TOKEN" ]; then printf 'Authorization: %s, Token="%s"' "$AUTH" "$TOKEN"
  else printf 'Authorization: %s' "$AUTH"; fi
}
get() { curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }
post() {
  curl -fsS -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"
}
del() { curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }

down() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "$NAME-config" "$NAME-cache" >/dev/null 2>&1 || true
}

# Empties the fixture folders. The hidden files stay, because Jellyfin skips a
# library path holding no files and keeps its items.
reset_fixtures() {
  rm -rf "${FED:?}" "${EXTRA:?}"
  mkdir -p "$FED/Serie" "$FED/Film" "$EXTRA/serie" "$EXTRA/film"
  touch "$FED/Serie/.jellymesh" "$FED/Film/.jellymesh" "$EXTRA/serie/.keep" "$EXTRA/film/.keep"
}

up() {
  down
  docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" -e TZ=Europe/Rome \
    -e "JELLYFIN_Serilog__MinimumLevel__Default=${LOG_LEVEL:-Information}" \
    -v "$NAME-config:/config" -v "$NAME-cache:/cache" \
    -v "$REPO_MEDIA:/media:ro" -v "$FED:/federata" -v "$EXTRA:/local-extra" \
    "$IMAGE" >/dev/null
  TOKEN=
  local i
  for i in $(seq 1 120); do
    get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && break
    sleep 1
  done
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  get /Startup/User >/dev/null
  post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}'
  post /Startup/Complete
  TOKEN=$(post /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken)
  USER_ID=$(get /Users/Me | jq -r .Id)
}

# add_library <name> <type> <options json> <path>...
add_library() {
  local name=$1 type=$2 options=$3 query path
  shift 3
  query="name=$name&collectionType=$type&refreshLibrary=false"
  for path in "$@"; do query+="&paths=$path"; done
  post "/Library/VirtualFolders?$query" "$options"
}

scan_task() { get /ScheduledTasks | jq '.[] | select(.Key == "RefreshLibrary")'; }

scan() {
  local last new i
  last=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post /Library/Refresh
  for i in $(seq 1 300); do
    new=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && return
    sleep 1
  done
  echo "scan did not finish" >&2
  return 1
}

# Copies the database out, because the API collapses what the rows hold.
sql() {
  local f
  for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do
    docker cp "$NAME:/config/data/$f" "$DB_COPY/$f" 2>/dev/null || rm -f "${DB_COPY:?}/${f:?}"
  done
  sqlite3 -header -column "$DB_COPY/jellyfin.db" "$1"
}

# Same as sql, without headers, for values a script reads back.
sql_raw() {
  sql "select 1;" >/dev/null
  sqlite3 -noheader -list "$DB_COPY/jellyfin.db" "$1"
}
