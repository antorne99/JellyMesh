#!/usr/bin/env bash
# Integration 3 and 4 of the 0.6 spec (F27, F30, §10 rows 424, 427, 466): the plugin database on a real server.
# 4: the stock /Plugins/{id}/Configuration answers 404, and the settings live behind the plugin's own API.
# 3: a nightly copy, then a damaged file restarts from it with the restore banner; a schema newer than the build leaves
# the file intact and suspends JellyMesh; a data folder Jellyfin cannot write suspends it until "Retry" finds it writable.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-store] [PORT=18530] store.sh   (KEEP=1 leaves them up)
set -euo pipefail
PREFIX=${PREFIX:-jm-store}
PORT=${PORT:-18530}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
[ "${KEEP:-}" = 1 ] || trap down EXIT

store() { get local /JellyMesh/Admin/Store; }
state() { store | jq -r .State; }
interval() { get local /JellyMesh/Admin/Settings | jq -r .SyncIntervalSeconds; }
in_data() { docker run --rm -v "$PREFIX-local-config:/config" --entrypoint sh "$IMAGE" -c "$1"; }
db_sha() { in_data "sha256sum $DATA/jellymesh.db | cut -d' ' -f1"; }
# relaunch [docker options...]: the same volumes in a new container, so its user can change.
relaunch() {
  docker rm -f "$PREFIX-local" >/dev/null
  docker run -d --name "$PREFIX-local" -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway -e TZ=Europe/Rome "$@" \
    -v "$PREFIX-local-config:/config" -v "$PREFIX-local-cache:/cache" -v "$PREFIX-local-fed:/federata" -v "$PREFIX-media:/media" \
    "$IMAGE" >/dev/null
  wait_ready local
  login local
}
# run_task <key>: runs a scheduled task and waits for it to end.
run_task() {
  local id last new i
  id=$(get local /ScheduledTasks | jq -r --arg k "$1" '.[] | select(.Key == $k) | .Id')
  last=$(get local "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post local "/ScheduledTasks/Running/$id" >/dev/null
  for i in $(seq 1 300); do
    new=$(get local "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ -n "$new" ] && [ "$new" != "$last" ] && return 0
    sleep 1
  done
  echo "task $1 did not end" >&2
  return 1
}
viewer_code() { # the status a user who is not an admin gets on a stock route
  curl -s -o /dev/null -w '%{http_code}' -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$(cat "$WORK/token-viewer-plain")\"" "http://127.0.0.1:$PORT/Users/Me"
}
viewer_token() {
  curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $AUTH" \
    -d '{"Username":"viewer","Pw":"jellymesh-test"}' "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/token-viewer-plain"
}

down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film
user local viewer >/dev/null
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
viewer_token

echo "--- 4: no stock configuration"
get local "/Plugins/$PLUGIN/Configuration" >/dev/null
check "/Plugins/{id}/Configuration answers 404" 404 "$(status)"
req local POST "/Plugins/$PLUGIN/Configuration" '{"FederatedFolderPath":"/tmp"}' >/dev/null
check "a stock save of the configuration is not taken" true "$([ "$(status)" != 204 ] && [ "$(status)" != 200 ] && echo true || echo false)"
check "no configuration XML is written" gone "$(docker exec "$PREFIX-local" sh -c "test -e '$OLD_XML' && echo there || echo gone")"
check "the settings save through the plugin's API" 200 "$(settings local '.FederatedFolderPath = "/federata"')"
check "and read back from it" /federata "$(get local /JellyMesh/Admin/Settings | jq -r .FederatedFolderPath)"
get local "/web/configurationpage?name=JellyMesh" >/dev/null
check "the page is served" 200 "$(status)"
check "the database is healthy" Healthy "$(state)"
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
import_all local "$id"
sync_now local
check "the server syncs" true "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[].Movies > 0] | all')"

echo "--- 3: a nightly copy, then a damaged file"
run_task JellyMeshReconciliation
copies=$(in_data "ls $DATA | grep -c '^jellymesh.db.backup-' || true")
check "the reconciliation made a copy" 1 "$copies"
check "the copy is its owner's only" "-rw-------" "$(in_data "ls -l $DATA/jellymesh.db.backup-* | cut -c1-10")"
check "the copy holds no key" 0 "$(in_data "ls $DATA | grep -c 'backup.*key' || true")"
settings local '.SyncIntervalSeconds = 1200' >/dev/null
check "a setting changed after the copy" 1200 "$(interval)"
docker stop "$PREFIX-local" >/dev/null
in_data "printf 'this is not a database, it was damaged on purpose' | dd of=$DATA/jellymesh.db bs=1 seek=0 conv=notrunc 2>/dev/null; rm -f $DATA/jellymesh.db-wal $DATA/jellymesh.db-shm"
docker start "$PREFIX-local" >/dev/null
wait_ready local
login local
check "the database is healthy again" Healthy "$(state)"
check "the banner names the copy it restarted from" true "$(store | jq '.Recovery.CopyDate != null and (.Recovery.DamagedFile | startswith("jellymesh.db.damaged-"))')"
check "the status carries the same banner" true "$(get local /JellyMesh/Admin/Status | jq '.Store.Recovery.CopyDate != null')"
check "the damaged file is set aside" 1 "$(in_data "ls $DATA | grep -c '^jellymesh.db.damaged-[0-9]*$' || true")"
check "the setting is back as the copy had it" 900 "$(interval)"
check "the server list is the copy's" "$id" "$(get local /JellyMesh/Admin/Status | jq -r '.Servers[].Id')"
sync_now local
check "its saved login still works" true "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[] | .Error == null] | all')"
check "a user is served" 200 "$(viewer_code)"
post local /JellyMesh/Admin/Store/Recovery/Dismiss >/dev/null
check "the banner goes once dismissed" null "$(store | jq -c .Recovery)"

echo "--- 3: a schema newer than this build"
docker stop "$PREFIX-local" >/dev/null
docker run --rm -v "$PREFIX-local-config:/config" "$PW_IMAGE" python3 -c '
import sqlite3, sys
c = sqlite3.connect(sys.argv[1]); c.execute("PRAGMA user_version = 999"); c.commit(); c.close()' "$DATA/jellymesh.db"
before=$(db_sha)
docker start "$PREFIX-local" >/dev/null
wait_ready local
login local
check "the database says it is newer" NewerSchema "$(state)"
check "the reason names the version" true "$(store | jq '.Reason | contains("999")')"
viewer_token
check "a user who is not an admin gets 503" 503 "$(viewer_code)"
post local /JellyMesh/Admin/Sync >/dev/null
check "a sync is refused as suspended" "409 Suspended" "$(status) $(jq -r .Failure "$WORK/body")"
docker stop "$PREFIX-local" >/dev/null
check "the file is left as it was" "$before" "$(db_sha)"

echo "--- 3: a data folder Jellyfin cannot write"
# Back to the version the build knows: the one the copy holds.
latest=$(in_data "ls $DATA/jellymesh.db.backup-* | head -1" | xargs -I{} docker run --rm -v "$PREFIX-local-config:/config:ro" "$PW_IMAGE" python3 -c 'import sqlite3,sys; print(sqlite3.connect("file:"+sys.argv[1]+"?mode=ro", uri=True).execute("PRAGMA user_version").fetchone()[0])' {})
docker run --rm -v "$PREFIX-local-config:/config" "$PW_IMAGE" python3 -c '
import sqlite3, sys
c = sqlite3.connect(sys.argv[1]); c.execute("PRAGMA user_version = " + sys.argv[2]); c.commit(); c.close()' "$DATA/jellymesh.db" "$latest"
# Jellyfin as an ordinary user, so a folder without write permission stops it as it would on a real install.
in_data "chown -R 1000:1000 /config; chmod 0500 $DATA"
docker start "$PREFIX-local" >/dev/null
docker exec "$PREFIX-local" chown -R 1000:1000 /cache /federata /media
relaunch --user 1000:1000
check "the database is unavailable" Unavailable "$(state)"
check "the reason is shown" true "$(store | jq '.Reason != null and (.Reason | length) > 0')"
viewer_token
check "a user who is not an admin gets 503" 503 "$(viewer_code)"
post local /JellyMesh/Admin/Store/Retry >/dev/null
check "Retry with the folder still read-only leaves it unavailable" Unavailable "$(state)"
docker exec -u 0 "$PREFIX-local" chmod 0700 "$DATA"
check "Retry once the folder is writable opens it" Healthy "$(post local /JellyMesh/Admin/Store/Retry | jq -r .State)"
check "the setting is still there" 900 "$(interval)"
sync_now local
check "the server syncs again" true "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[].Movies > 0] | all')"
check "a user is served again" 200 "$(viewer_code)"

exit "$FAILED"
