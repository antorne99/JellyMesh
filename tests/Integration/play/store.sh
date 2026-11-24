#!/usr/bin/env bash
# Integration 3 and 4 of the 0.6 spec (F27, F30, §10 rows 424, 427, 466): the plugin database on a real server.
# 4: the stock /Plugins/{id}/Configuration answers 404, and the settings live behind the plugin's own API.
# 3: a nightly copy, then a damaged file restarts from it with the restore banner; a schema newer than the build leaves
# the file intact and suspends JellyMesh; a data folder Jellyfin cannot write suspends it until "Retry" finds it writable.
# Rows 512, 523-526 and 536: damage found while the server runs restarts from the copy; a broken index is rebuilt; a fourth
# damage in 24 hours waits for "Retry", past a restart, with no new remote playback or peer access, and open streams go on.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-store] [PORT=18530] store.sh   (KEEP=1 leaves them up)
set -euo pipefail
PREFIX=${PREFIX:-jm-store}
PORT=${PORT:-18530}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[]}}'
PEER_AUTH="MediaBrowser Client=\"JellyMeshPlay\", Device=\"friend\", DeviceId=\"$PREFIX-friend\", Version=\"0.1\""
TITLE="Tears of Steel"
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
set_aside() { in_data "ls $DATA | grep -cE '^jellymesh.db.damaged-[0-9]+(-[0-9]+)?$' || true"; }
# damage: overwrites every page of the database and of its log while the server runs.
damage() {
  docker exec "$PREFIX-local" sh -c "cd $DATA; for f in jellymesh.db jellymesh.db-wal; do s=\$(stat -c %s \$f 2>/dev/null || echo 0)
    [ \$s = 0 ] || dd if=/dev/urandom of=\$f bs=4096 count=\$(((s + 4095) / 4096)) conv=notrunc 2>/dev/null; done"
}
# damaged_to <state> <files set aside>: damages it, uses it, and waits until it is in that state with those files.
damaged_to() {
  local i
  damage
  post local /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 60); do
    [ "$(state)" = "$1" ] && [ "$(set_aside)" = "$2" ] && break
    sleep 1
  done
  echo "$(state) $(set_aside)"
}
# playback: the status and the number of sources of a PlaybackInfo of the federated film, whose answer stays in $WORK/playback.
playback() {
  post local "/Items/$film/PlaybackInfo?UserId=$(user_id local admin)&AutoOpenLiveStream=false" "$DIRECT" >"$WORK/playback"
  echo "$(status) $(jq -r '(.MediaSources // []) | length' "$WORK/playback" 2>/dev/null || true)"
}
film_code() { # the status of the first bytes of the federated film as it is, for the admin
  curl -s -o /dev/null -m 60 -w '%{http_code}' -H 'Range: bytes=0-99' "http://127.0.0.1:$PORT/Videos/$film/stream?static=true&mediaSourceId=$film&ApiKey=$(token_of local)"
}
peer_token() {
  curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $PEER_AUTH" \
    -d '{"Username":"friend","Pw":"jellymesh-test"}' "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/token-friend"
}
peer_code() { # the status the connected server gets for the libraries shared with it
  curl -s -o /dev/null -w '%{http_code}' -H "$ACCEPT" -H "Authorization: $PEER_AUTH, Token=\"$(cat "$WORK/token-friend")\"" "http://127.0.0.1:$PORT/JellyMesh/Provider/Libraries"
}
restores_logged() { docker logs "$PREFIX-local" 2>&1 | grep -c 'Check the servers and peers' || true; }
integrity() { db_query local 'PRAGMA integrity_check'; }
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

scan local
film=$(stub_item local "$TITLE")
check "the federated film is in a library" true "$([ -n "$film" ] && echo true || echo false)"
check "and plays" "200 1" "$(playback)"
friend=$(user local friend)
shared=$(get local /JellyMesh/Admin/Peers/Options | jq -r '.Libraries[] | select(.Name == "Film") | .Id')
req local PUT "/JellyMesh/Admin/Peers/$friend" "$(jq -nc --arg l "$shared" '{SharedLibraryIds: [$l], MaxStreams: 1}')" >/dev/null
check "a connected server is added" 204 "$(status)"
peer_token
check "and is served" 200 "$(peer_code)"

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

echo "--- damage found while the server runs"
settings local '.SyncIntervalSeconds = 1200' >/dev/null
check "a setting changed after the copy" 1200 "$(interval)"
asked=$(restores_logged)
check "it restarts from the copy, with no restart of the server" "Healthy 2" "$(damaged_to Healthy 2)"
check "the banner names the copy it restarted from" true "$(store | jq '.Recovery.CopyDate != null and (.Recovery.DamagedFile | startswith("jellymesh.db.damaged-"))')"
check "the setting is back as the copy had it" 900 "$(interval)"
check "the server list is the copy's" "$id" "$(get local /JellyMesh/Admin/Status | jq -r '.Servers[].Id')"
check "the log asks once more to check the servers and the peers" $((asked + 1)) "$(restores_logged)"
sync_now local
check "its saved login still works" true "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[] | .Error == null] | all')"
check "the federated film plays" "200 1" "$(playback)"
check "the connected server is served" 200 "$(peer_code)"

echo "--- a broken index"
docker stop "$PREFIX-local" >/dev/null
docker run --rm -v "$PREFIX-local-config:/config" "$PW_IMAGE" python3 -c '
import sqlite3, sys
c = sqlite3.connect(sys.argv[1], isolation_level=None)
for step in ("CREATE TABLE proof (value TEXT, other TEXT)", "CREATE INDEX proof_index ON proof (value)", "INSERT INTO proof VALUES (1, 2)",
             "PRAGMA writable_schema = ON"):
    c.execute(step)
c.execute("UPDATE sqlite_master SET sql = ? WHERE name = ?", ("CREATE INDEX proof_index ON proof (other)", "proof_index"))
c.close()' "$DATA/jellymesh.db"
docker start "$PREFIX-local" >/dev/null
wait_ready local
login local
check "the check at open does not see it" "Healthy 2" "$(state) $(set_aside)"
check "the full check does" true "$(integrity | grep -q proof_index && echo true || echo false)"
run_task JellyMeshReconciliation
check "the nightly copy rebuilds it" ok "$(integrity)"
check "with no recovery" "Healthy 2" "$(state) $(set_aside)"
check "the log says so" true "$(docker logs "$PREFIX-local" 2>&1 | grep -q 'The indexes of jellymesh.db did not match' && echo true || echo false)"
check "and the copy is kept" 2 "$(in_data "ls $DATA | grep -c '^jellymesh.db.backup-' || true")"

echo "--- more than 3 recoveries in 24 hours"
check "the third damage still restarts from the copy" "Healthy 3" "$(damaged_to Healthy 3)"
check "the federated film plays" "200 1" "$(playback)"
curl -sS -o "$WORK/read" --limit-rate 500k -m 180 -w '%{http_code} %{exitcode}' "http://127.0.0.1:$PORT$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/playback")" >"$WORK/read-status" 2>/dev/null &
reader=$!
sleep 3
check "the fourth leaves it damaged" "Damaged 3" "$(damaged_to Damaged 3)"
check "the reason names the limit" true "$(store | jq '.Reason | contains("3 times in 24 hours")')"
check "the wait for Retry is on disk" there "$(in_data "test -e $DATA/jellymesh.db.held && echo there || echo gone")"
post local /JellyMesh/Admin/Sync >/dev/null
check "a sync is refused as suspended" "409 Suspended" "$(status) $(jq -r .Failure "$WORK/body")"
check "no new remote playback" "503 0" "$(playback)"
check "no peer access" 503 "$(peer_code)"
check "a local user is served" 200 "$(viewer_code)"
check "the stream was still open" running "$(kill -0 "$reader" 2>/dev/null && echo running || echo ended)"
wait "$reader" || true
check "and goes on to its end" "200 0" "$(cat "$WORK/read-status")"
check "with the whole file" "$(shasum -a 256 <"$(ls "$MEDIA/remoto-standard/film/$TITLE (2012)/"*.mkv)" | cut -d' ' -f1)" "$(shasum -a 256 <"$WORK/read" | cut -d' ' -f1)"
docker restart "$PREFIX-local" >/dev/null
wait_ready local
login local
check "a restart leaves it damaged" "Damaged 3" "$(state) $(set_aside)"
check "no peer access" 503 "$(peer_code)"
check "a user who is not an admin gets 503" 503 "$(viewer_code)"
check "the film opens for no one, an admin included" refused "$(film_code | grep -q '^2' && echo opened || echo refused)"
first=$(in_data "ls $DATA | grep -E '^jellymesh.db.damaged-[0-9]+(-[0-9]+)?$' | sort | head -1")
check "Retry restarts from the copy" Healthy "$(post local /JellyMesh/Admin/Store/Retry | jq -r .State)"
check "the last 3 damaged files stay" "3 gone" "$(set_aside) $(in_data "test -e $DATA/$first && echo there || echo gone")"
check "nothing waits for Retry" gone "$(in_data "test -e $DATA/jellymesh.db.held && echo there || echo gone")"
check "the setting is the copy's" 900 "$(interval)"
sync_now local
check "the server syncs again" true "$(get local /JellyMesh/Admin/Sync | jq '[.Servers[] | .Error == null] | all')"
check "the federated film plays" "200 1" "$(playback)"
check "and opens for the admin" 206 "$(film_code)"
check "the connected server is served" 200 "$(peer_code)"
post local /JellyMesh/Admin/Store/Recovery/Dismiss >/dev/null

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
