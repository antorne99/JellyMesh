# Helpers for the private instance jm-mat. Source this file. NAME and PORT move it, STANDARD_URL and JELLYMESH_URL name
# the two remotes (by default remoto-standard and remoto-jellymesh of the shared stack).
set -euo pipefail
NAME=${NAME:-jm-mat}
PORT=${PORT:-18310}
STANDARD_URL=${STANDARD_URL:-http://host.docker.internal:8097}
JELLYMESH_URL=${JELLYMESH_URL:-http://host.docker.internal:8098}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
WT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)
MEDIA=${MEDIA:-$WT/tests/Integration/media}  # gitignored: in a worktree, point it at the main checkout
BASE=http://127.0.0.1:$PORT
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshMat", Device="mat", DeviceId="jm-mat-e2e", Version="0.1"'
TOKEN_FILE=${TMPDIR:-/tmp}/$NAME-token
TOKEN=$(cat "$TOKEN_FILE" 2>/dev/null || true)
auth_header() {
  if [ -n "$TOKEN" ]; then printf 'Authorization: %s, Token="%s"' "$AUTH" "$TOKEN"
  else printf 'Authorization: %s' "$AUTH"; fi
}
get() { curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }
post() { curl -fsS -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"; }
put() { curl -fsS -X PUT -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "$2" "$BASE$1"; }
wait_ready() {
  local i
  for i in $(seq 1 120); do
    get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return
    sleep 1
  done
  echo "not ready" >&2; return 1
}
login() { # <user> <password>
  TOKEN=
  TOKEN=$(post /Users/AuthenticateByName "$(jq -n --arg u "$1" --arg p "$2" '{Username:$u,Pw:$p}')" | jq -r .AccessToken)
  echo "$TOKEN" >"$TOKEN_FILE"
}
scan_task() { get /ScheduledTasks | jq '.[] | select(.Key == "RefreshLibrary")'; }
scan() {
  local last new i
  last=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post /Library/Refresh
  for i in $(seq 1 300); do
    new=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && { sleep 2; return; }
    sleep 1
  done
  echo "scan did not finish" >&2; return 1
}
# Waits for a queued scan to run and finish.
scan_settle() {
  local i state
  sleep 3
  for i in $(seq 1 120); do
    state=$(scan_task | jq -r .State)
    [ "$state" = Idle ] && { sleep 2; return; }
    sleep 1
  done
  echo "scan did not settle" >&2; return 1
}
# Waits for a running sync (saving a server starts one, and a second is refused with 409), then runs a full one.
sync_full() {
  local i
  for i in $(seq 1 180); do [ "$(get /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && break; sleep 1; done
  post /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 180); do
    [ "$(get /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return
    sleep 1
  done
  echo "sync did not finish" >&2; return 1
}
applog() { docker exec "$NAME" sh -c 'cat /config/log/log_*.log'; }
sql_raw() {
  local d=${TMPDIR:-/tmp}/$NAME-db f
  mkdir -p "$d"
  for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do
    docker cp "$NAME:/config/data/$f" "$d/$f" 2>/dev/null || rm -f "$d/$f"
  done
  sqlite3 -noheader -list "$d/jellyfin.db" "$1"
}
