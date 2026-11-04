# Helpers for the private instance jm-mat. Source this file.
set -euo pipefail
NAME=jm-mat
PORT=18310
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
WT=/Users/antoniotornesello/Documents/Progetti/Personal/JellyMesh/.claude/worktrees/agent-a6b83772c90af3ed4
MEDIA=/Users/antoniotornesello/Documents/Progetti/Personal/JellyMesh/tests/Integration/media
BASE=http://127.0.0.1:$PORT
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshMat", Device="mat", DeviceId="jm-mat-e2e", Version="0.1"'
TOKEN_FILE=/private/tmp/claude-501/-Users-antoniotornesello-Documents-Progetti-Personal-JellyMesh/03611a95-0dbb-4ea2-acce-7040c4e03f3a/scratchpad/mat/token
TOKEN=$(cat "$TOKEN_FILE" 2>/dev/null || true)
auth_header() {
  if [ -n "$TOKEN" ]; then printf 'Authorization: %s, Token="%s"' "$AUTH" "$TOKEN"
  else printf 'Authorization: %s' "$AUTH"; fi
}
get() { curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE$1"; }
post() { curl -fsS -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d "${2:-}" "$BASE$1"; }
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
sync_full() {
  local i
  post /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 180); do
    [ "$(get /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return
    sleep 1
  done
  echo "sync did not finish" >&2; return 1
}
applog() { docker exec "$NAME" sh -c 'cat /config/log/log_*.log'; }
sql_raw() {
  local d=/private/tmp/claude-501/-Users-antoniotornesello-Documents-Progetti-Personal-JellyMesh/03611a95-0dbb-4ea2-acce-7040c4e03f3a/scratchpad/mat/db f
  mkdir -p "$d"
  for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do
    docker cp "$NAME:/config/data/$f" "$d/$f" 2>/dev/null || rm -f "$d/$f"
  done
  sqlite3 -noheader -list "$d/jellyfin.db" "$1"
}
