#!/usr/bin/env bash
# Decision 413: removing a server ends what plays from it at once, then ends its login on the remote within a short time.
# A remote that does not answer keeps the device listed, the removal stays fast and the log shows no token or address.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-rml] [PORT=18480] removal-logout.sh   (KEEP=1 leaves the containers up)
set -euo pipefail
PREFIX=${PREFIX:-jm-rml}
PORT=${PORT:-18480}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

port_of() { case $1 in local) echo "$PORT" ;; remote) echo $((PORT + 1)) ;; esac; }
name_of() { echo "$PREFIX-$1"; }
down() {
  docker unpause "$PREFIX-remote" >/dev/null 2>&1 || true
  docker rm -f "$PREFIX-local" "$PREFIX-remote" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" \
    "$PREFIX-remote-config" "$PREFIX-remote-cache" "$PREFIX-remote-fed" "$PREFIX-local-media" >/dev/null 2>&1 || true
}
[ "${KEEP:-}" = 1 ] || trap down EXIT

PROFILE_DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
FILM="JM Confronto Risoluzione"
now() { perl -MTime::HiRes=time -e 'printf "%.2f\n", time'; }
since() { perl -e "printf '%.1f', $(now) - $1"; }
under() { perl -e "exit !($1 < $2)" && echo yes || echo "no ($1 s)"; }

# device_listed <server id>: whether the remote lists the plugin's device for that server.
device_listed() {
  get remote /Devices | jq -r --arg d "jellymesh-${1//-/}" '[.Items[] | select(.Id == $d)] | length > 0'
}
# remote_token <server id>: the token the remote holds for the plugin's device, read from its database.
remote_token() {
  local db="$WORK/remote-db"
  rm -rf "$db" && mkdir -p "$db"
  docker exec "$PREFIX-remote" sh -c 'cd /config/data && ls jellyfin.db*' | while read -r f; do docker cp "$PREFIX-remote:/config/data/$f" "$db/$f" >/dev/null; done
  sqlite3 "$db/jellyfin.db" "select AccessToken from Devices where DeviceId = 'jellymesh-${1//-/}' order by Id desc limit 1;"
}
token_works() { curl -s -o /dev/null -w '%{http_code}' -H "Authorization: MediaBrowser Token=\"$1\"" "http://127.0.0.1:$(port_of remote)/System/Info"; }
# clean_log <since> <token>: the plugin's log since then must hold no token and no remote address.
clean_log() {
  local log
  log=$(docker logs --since "$1" "$PREFIX-local" 2>&1)
  check "the log holds no token" 0 "$(grep -cF "$2" <<<"$log" || true)"
  check "the log holds no remote address" 0 "$(grep -cE "host\.docker\.internal|:$(port_of remote)" <<<"$log" || true)"
}
log_line() { docker logs --since "$1" "$PREFIX-local" 2>&1 | { grep -iF "$2" || true; } | tail -1 | sed -E 's/^.*\] //'; }
remove() { # remove <server id>: prints the status
  req local DELETE "/JellyMesh/Admin/Servers/$1" >/dev/null
  status
}
connect() { # connect -> server id, synced and logged in
  local id
  id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
  import_all local "$id"
  sync_now local
  echo "$id"
}

down
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-local-media:/media"
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
# Small socket buffers in the local's network: by default they hold megabytes that a slow reader drains long after the end.
docker run --rm --name "$PREFIX-sysctl" --privileged --network "container:$PREFIX-local" --entrypoint sh "$IMAGE" -c \
  'echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_rmem && echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_wmem'
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(connect)
library local Film movies "$(server_folder "$id")/Film"
scan local

echo "== a live removal ends the streams, then the login"
item=$(stub_item local "$FILM")
check "the title is federated" true "$([ -n "$item" ] && echo true || echo false)"
check "the remote lists the plugin's device" true "$(device_listed "$id")"
token=$(remote_token "$id")
check "the plugin's token works on the remote" 200 "$(token_works "$token")"

opened=$(post local "/Items/$item/PlaybackInfo" "$PROFILE_DIRECT")
proxy="http://127.0.0.1:8096$(jq -r '.MediaSources[0].TranscodingUrl' <<<"$opened")"
static="http://127.0.0.1:8096/Videos/$item/stream?static=true&ApiKey=$(token_of local)"
size=$(find "$MEDIA/remoto-standard/film/$FILM (2026)" -name '*.mkv' -exec stat -f %z {} \; | head -1)
# The readers run in the local container, beside the plugin, whose small socket buffers hold little after the end.
slow_read() { docker exec "$PREFIX-local" curl -s -o /dev/null --limit-rate 200k -m 120 -w '%{http_code} %{size_download} exit %{exitcode}' "$1"; }
slow_read "$proxy" >"$WORK/read-proxy" 2>/dev/null &
proxy_reader=$!
slow_read "$static" >"$WORK/read-static" 2>/dev/null &
static_reader=$!
sleep 4
check "the proxied stream is still reading" true "$(kill -0 $proxy_reader 2>/dev/null && echo true || echo false)"
check "the stock static stream is still reading" true "$(kill -0 $static_reader 2>/dev/null && echo true || echo false)"
since_log=$(date -u +%Y-%m-%dT%H:%M:%SZ)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
removal=$(since "$t")
wait $proxy_reader || true
proxy_end=$(since "$t")
wait $static_reader || true
static_end=$(since "$t")
echo "    removal took $removal s; the proxied read ended after $proxy_end s ($(cat "$WORK/read-proxy")), the static one after $static_end s ($(cat "$WORK/read-static")), of $size bytes"
check "the removal is fast" yes "$(under "$removal" 5)"
check "the proxied stream ends at once" yes "$(under "$proxy_end" 5)"
check "the static stream ends at once" yes "$(under "$static_end" 5)"
check "the proxied stream did not finish" true "$([ "$(cut -d' ' -f2 "$WORK/read-proxy")" -lt "$size" ] && echo true || echo false)"
check "the static stream did not finish" true "$([ "$(cut -d' ' -f2 "$WORK/read-static")" -lt "$size" ] && echo true || echo false)"
check "the remote no longer lists the plugin's device" false "$(device_listed "$id")"
check "the old token is refused on the remote" 401 "$(token_works "$token")"
echo "    log: $(log_line "$since_log" "removed remote server")"
clean_log "$since_log" "$token"

# dead_removal <how>: removes a server whose remote is paused or stopped; its id goes to $id.
dead_removal() {
  local token t removal line
  id=$(connect)
  token=$(remote_token "$id")
  check "the remote lists the new device" true "$(device_listed "$id")"
  docker "$1" "$PREFIX-remote" >/dev/null
  since_log=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  t=$(now)
  check "the removal answers" 204 "$(remove "$id")"
  removal=$(since "$t")
  echo "    removal took $removal s"
  check "the removal is fast" yes "$(under "$removal" 8)"
  line=$(log_line "$since_log" "Could not end the login")
  echo "    log: $line"
  check "the failure is logged" true "$([ -n "$line" ] && echo true || echo false)"
  clean_log "$since_log" "$token"
}

echo "== a remote that does not answer: the logout is cut and the removal stays fast"
dead_removal pause
docker unpause "$PREFIX-remote" >/dev/null
# The forwarder held the request, so it may still reach the remote now: shown, not checked.
sleep 2
echo "    after the pause the remote lists the device: $(device_listed "$id")"

echo "== a stopped remote keeps the device listed and the removal stays fast"
dead_removal stop
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
login remote
check "the device stays listed on the remote" true "$(device_listed "$id")"

echo "== without a live login nothing is sent and the device stays"
id=$(connect)
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
since_log=$(date -u +%Y-%m-%dT%H:%M:%SZ)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
echo "    removal took $(since "$t") s"
echo "    log: $(log_line "$since_log" "removed remote server")"
check "the device stays listed on the remote" true "$(device_listed "$id")"

exit $FAILED
