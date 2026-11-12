#!/usr/bin/env bash
# Decision 42: the credential key moves from JELLYMESH_SECRET_KEY to a file. A consumer on 0.3.0.0 with the variable is
# upgraded to the current build with the variable still set, restarted without it, then loses its key file.
# usage: MEDIA=<main checkout>/tests/Integration/media keyfile.sh   (leaves the containers up; lib.sh has down)
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
OLD=$WORK/old-0.3.0.0
LEGACY=jm-play-test-key-0123456789abcdef0123456789

rm -rf "$OLD" && mkdir -p "$OLD"
git -C "$WT" archive v0.3.0.0 | tar -x -C "$OLD"

down
docker volume create "$PREFIX-media" >/dev/null
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote

START_ENV="-e JELLYMESH_SECRET_KEY=$LEGACY" start local "$PREFIX-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film /federata/Film
NUGET_VOLUME=jellymesh-nuget bash "$OLD/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
import_all local "$id"
sync_now local
scan local
stubs_before=$(docker exec "$PREFIX-local" sh -c 'find /federata/Film -name "*.strm" | sort')
check "0.3.0.0 made stubs" true "$([ -n "$stubs_before" ] && echo true || echo false)"
# blob: the saved password as the plugin XML holds it. The API never shows it.
blob() { docker exec "$PREFIX-local" sh -c 'grep -o "<EncryptedCredentials>[^<]*" /config/plugins/configurations/Jellyfin.Plugin.JellyMesh.xml'; }
blob_before=$(blob)

# relaunch [env...]: the same volumes in a new container, so its environment can change.
relaunch() {
  docker rm -f "$PREFIX-local" >/dev/null
  docker run -d --name "$PREFIX-local" -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway -e TZ=Europe/Rome "$@" \
    -v "$PREFIX-local-config:/config" -v "$PREFIX-local-cache:/cache" -v "$PREFIX-local-fed:/federata" -v "$PREFIX-media:/media" \
    "$IMAGE" >/dev/null
  wait_ready local
  login local
}

status_line() { get local /JellyMesh/Admin/Status | jq -r '"\(.KeyState) suspended=\(.Suspended) legacy=\(.LegacyKeySet) needs=\([.Servers[].NeedsPassword] | any)"'; }
key_file() { docker exec "$PREFIX-local" sh -c 'find /config -name credential.key | head -1'; }

# Upgrade with the variable still set: the credentials move to the file at the first start.
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local
check "after the upgrade the key works and the old variable is reported" "Ok suspended=false legacy=true needs=false" "$(status_line)"
check "the saved password was encrypted again" true "$([ "$(blob)" != "$blob_before" ] && echo true || echo false)"
check "the key file exists, readable by its owner only" "-rw-------" "$(docker exec "$PREFIX-local" sh -c "ls -l \"$(key_file)\" | cut -c1-10")"
check "the key is in no log" 0 "$(docker exec "$PREFIX-local" sh -c "grep -rlF \"\$(cat \"$(key_file)\")\" /config/log | wc -l" | tr -d ' ')"
check "a sync runs" 202 "$(post local /JellyMesh/Admin/Sync >/dev/null; status)"
sync_now local

# Without the variable: nothing changes.
relaunch
check "without the variable the key still works" "Ok suspended=false legacy=false needs=false" "$(status_line)"
check "a sync runs without the variable" 202 "$(post local /JellyMesh/Admin/Sync >/dev/null; status)"
sync_now local
check "the stubs are the same" "$stubs_before" "$(docker exec "$PREFIX-local" sh -c 'find /federata/Film -name "*.strm" | sort')"

# The key file is lost: the server asks for its password, and gets it.
docker exec "$PREFIX-local" sh -c "rm -f \"$(key_file)\""
relaunch -e JELLYMESH_SECRET_KEY=$LEGACY
check "a lost key file asks for the password, and the old variable cannot help" "Wrong suspended=true legacy=true needs=true" "$(status_line)"
check "sync is refused meanwhile" 409 "$(post local /JellyMesh/Admin/Sync >/dev/null; status)"
check "a wrong password is refused" 502 "$(post local "/JellyMesh/Admin/Servers/$id/Credentials" '{"Username":"jellymesh","Pw":"x","Password":"wrong"}' >/dev/null; status)"
check "the password entered again is accepted" 200 "$(post local "/JellyMesh/Admin/Servers/$id/Credentials" '{"Username":"jellymesh","Password":"jellymesh-test"}' >/dev/null; status)"
cp "$WORK/body" "$WORK/credentials-answer"
check "and the federation works again" "Ok suspended=false legacy=true needs=false" "$(status_line)"
check "the answer holds no password" 0 "$(grep -c jellymesh-test "$WORK/credentials-answer" || true)"
sync_now local
check "the stubs are still the same" "$stubs_before" "$(docker exec "$PREFIX-local" sh -c 'find /federata/Film -name "*.strm" | sort')"

echo "=== failed: $FAILED"
exit $FAILED
