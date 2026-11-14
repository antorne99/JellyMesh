#!/usr/bin/env bash
# Decision 413: removing a server ends what plays from it at once, then ends its login on the remote within a short time.
# A remote that does not answer keeps the device listed, the removal stays fast and the log shows no token or address.
# Decision 418: after a local restart there is no live login, so the removal logs in with the same device and out at once.
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
SIZE=$(wc -c <"$(find "$MEDIA/remoto-standard/film/$FILM (2026)" -name '*.mkv' | head -1)" | tr -d ' ')
now() { perl -MTime::HiRes=time -e 'printf "%.2f\n", time'; }
since() { perl -e "printf '%.1f', $(now) - $1"; }
under() { perl -e "exit !($1 < $2)" && echo yes || echo "no ($1)"; }
yes_no() { "$@" && echo true || echo false; }

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
stamp() { date -u +%Y-%m-%dT%H:%M:%SZ; }
remove() { # remove <server id>: prints the status
  req local DELETE "/JellyMesh/Admin/Servers/$1" >/dev/null
  status
}
connect() { # connect -> the id of the remote added again, synced, logged in and in the Film library
  local id
  id=$(add_remote local remote "http://host.docker.internal:$(port_of remote)" jellymesh)
  import_all local "$id"
  sync_now local
  link_folder local Film "$(server_folder "$id")/Film" >/dev/null
  scan local
  echo "$id"
}
transcoding() { # transcoding <true|false>: whether the plugin's user may transcode or remux on the remote
  local uid policy
  uid=$(user_id remote jellymesh)
  policy=$(get remote "/Users/$uid" | jq -c ".Policy | .EnableVideoPlaybackTranscoding = $1 | .EnableAudioPlaybackTranscoding = $1 | .EnablePlaybackRemuxing = $1")
  post remote "/Users/$uid/Policy" "$policy" >/dev/null
}
open() { post local "/Items/$1/PlaybackInfo" "$2"; } # open <item> <profile>: the PlaybackInfo answer
# open_elsewhere <item> <profile>: the same, from a second device of the admin, so that it keeps its own session.
open_elsewhere() {
  local auth="MediaBrowser Client=\"JellyMeshPlay\", Device=\"tv\", DeviceId=\"$PREFIX-tv\", Version=\"0.1\"" token
  token=$(curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $auth" \
    -d '{"Username":"admin","Pw":"jellymesh-test"}' "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken)
  curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $auth, Token=\"$token\"" \
    -d "$2" "http://127.0.0.1:$PORT/Items/$1/PlaybackInfo"
}
url_in() { echo "http://127.0.0.1:8096$(jq -r '.MediaSources[0].TranscodingUrl' <<<"$1")"; }
# The readers run in the local container, beside the plugin, whose small socket buffers hold little after the end.
inside() { docker exec "$PREFIX-local" curl "$@"; }
slow_read() { inside -s -o /dev/null --limit-rate 200k -m 120 -w '%{http_code} %{size_download} exit %{exitcode}' "$1"; }
quick() { inside -s -o /dev/null -m 30 -w '%{http_code} %{time_total}' "$@"; }
# entry <playlist url> [n]: the URL of the nth entry of a playlist, by default the first.
entry() {
  local line base
  line=$(inside -s -m 30 "$1" | tr -d '\r' | grep -v '^#' | sed -n "${2:-1}p")
  base=${1%%\?*}
  case $line in
    http*) echo "$line" ;;
    /*) echo "http://127.0.0.1:8096$line" ;;
    *) echo "${base%/*}/$line" ;;
  esac
}
ffmpegs() { docker exec "$PREFIX-local" sh -c 'for p in /proc/[0-9]*; do tr "\0" " " <$p/cmdline 2>/dev/null; echo; done' | grep -c '^/usr/lib/jellyfin-ffmpeg/ffmpeg' || true; }
reading() { kill -0 "$1" 2>/dev/null; }

down
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote

start local "$PREFIX-local-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
# Small socket buffers in the local's network: by default they hold megabytes that a slow reader drains long after the end.
docker run --rm --name "$PREFIX-sysctl" --privileged --network "container:$PREFIX-local" --entrypoint sh "$IMAGE" -c \
  'echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_rmem && echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_wmem'
login local
login remote
settings local '.FederatedFolderPath = "/federata"' >/dev/null

echo "== a live removal ends the streams, then the login"
id=$(connect)
item=$(stub_item local "$FILM")
check "the title is federated" true "$(yes_no [ -n "$item" ])"
check "the remote lists the plugin's device" true "$(device_listed "$id")"
token=$(remote_token "$id")
check "the plugin's token works on the remote" 200 "$(token_works "$token")"

proxy=$(url_in "$(open "$item" "$PROFILE_DIRECT")")
static="http://127.0.0.1:8096/Videos/$item/stream?static=true&ApiKey=$(token_of local)"
hls=$(url_in "$(open_elsewhere "$item" "$PROFILE_HLS")")
check "the remote transcode opens through the proxy" 200 "$(quick "$hls" | cut -d' ' -f1)"
variant=$(entry "$hls")
check "and its variant playlist" 200 "$(quick "$variant" | cut -d' ' -f1)"
slow_read "$proxy" >"$WORK/read-proxy" 2>/dev/null &
proxy_reader=$!
slow_read "$static" >"$WORK/read-static" 2>/dev/null &
static_reader=$!
sleep 4
check "the proxied stream is still reading" true "$(yes_no reading $proxy_reader)"
check "the stock static stream is still reading" true "$(yes_no reading $static_reader)"
since_log=$(stamp)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
removal=$(since "$t")
wait $proxy_reader || true
proxy_end=$(since "$t")
wait $static_reader || true
static_end=$(since "$t")
echo "    removal took $removal s; the proxied read ended after $proxy_end s ($(cat "$WORK/read-proxy")), the static one after $static_end s ($(cat "$WORK/read-static")), of $SIZE bytes"
check "the removal is fast" yes "$(under "$removal" 5)"
check "the proxied stream ends at once" yes "$(under "$proxy_end" 5)"
check "the static stream ends at once" yes "$(under "$static_end" 5)"
check "the proxied stream did not finish" true "$(yes_no [ "$(cut -d' ' -f2 "$WORK/read-proxy")" -lt "$SIZE" ])"
check "the static stream did not finish" true "$(yes_no [ "$(cut -d' ' -f2 "$WORK/read-static")" -lt "$SIZE" ])"
again=$(quick -H 'Range: bytes=1000000-' "$proxy")
echo "    the proxied stream asked again: $again"
check "the proxied stream asked again is refused" true "$(yes_no [ "${again%% *}" -ge 400 ])"
again=$(quick "$variant")
echo "    the remote transcode asked again: $again"
check "the remote transcode asked again is refused" true "$(yes_no [ "${again%% *}" -ge 400 ])"
check "the remote no longer lists the plugin's device" false "$(device_listed "$id")"
check "the old token is refused on the remote" 401 "$(token_works "$token")"
echo "    log: $(log_line "$since_log" "removed remote server")"
clean_log "$since_log" "$token"

echo "== a removal ends the local transcode of the remote's file"
transcoding false
id=$(connect)
item=$(stub_item local "$FILM")
master=$(url_in "$(open "$item" "$PROFILE_HLS")")
echo "    master: ${master%%\?*}"
check "the stub plays through a local transcode" true "$(yes_no [ "${master#*/JellyMesh/}" = "$master" ])"
variant=$(entry "$master")
segment=$(entry "$variant")
check "its first segment is served" 200 "$(quick "$segment" | cut -d' ' -f1)"
check "ffmpeg runs" true "$(yes_no [ "$(ffmpegs)" -gt 0 ])"
next=$(entry "$variant" 4)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
for _ in $(seq 1 40); do [ "$(ffmpegs)" = 0 ] && break; sleep 0.25; done
gone=$(since "$t")
echo "    ffmpeg ended $gone s after the removal began"
check "ffmpeg ends at once" yes "$(under "$gone" 5)"
echo "    a later segment asked after the removal: $(quick "$next"); ffmpeg running then: $(ffmpegs)"
transcoding true

echo "== a remote that does not answer: the stops and the logout are cut and the removal stays fast"
id=$(connect)
item=$(stub_item local "$FILM")
token=$(remote_token "$id")
hls=$(url_in "$(open "$item" "$PROFILE_HLS")")
check "a remote transcode is open" 200 "$(quick "$hls" | cut -d' ' -f1)"
docker pause "$PREFIX-remote" >/dev/null
since_log=$(stamp)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
removal=$(since "$t")
docker unpause "$PREFIX-remote" >/dev/null
echo "    removal took $removal s"
check "the removal stays within the stop and logout limits" yes "$(under "$removal" 8)"
line=$(log_line "$since_log" "Could not end the login")
echo "    log: $line"
echo "    stop: $(log_line "$since_log" "remote transcode")"
check "the failure is logged" true "$(yes_no [ -n "$line" ])"
clean_log "$since_log" "$token"
# The port forwarder held the request, so it may still reach the remote now: shown, not checked.
sleep 2
echo "    after the pause the remote lists the device: $(device_listed "$id")"

echo "== a stopped remote keeps the device listed and the removal stays fast"
id=$(connect)
token=$(remote_token "$id")
docker stop "$PREFIX-remote" >/dev/null
since_log=$(stamp)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
removal=$(since "$t")
echo "    removal took $removal s"
check "the removal is fast" yes "$(under "$removal" 5)"
line=$(log_line "$since_log" "Could not end the login")
echo "    log: $line"
check "the failure is logged" true "$(yes_no [ -n "$line" ])"
clean_log "$since_log" "$token"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
login remote
check "the device stays listed on the remote" true "$(device_listed "$id")"

echo "== without a live login the removal logs in and out with the same device"
id=$(connect)
token=$(remote_token "$id")
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "the remote lists the plugin's device" true "$(device_listed "$id")"
since_log=$(stamp)
t=$(now)
check "the removal answers" 204 "$(remove "$id")"
removal=$(since "$t")
echo "    removal took $removal s"
check "the removal is fast" yes "$(under "$removal" 5)"
line=$(log_line "$since_log" "removed remote server")
echo "    log: $line"
check "the removal logged in again to end the device" true "$(yes_no grep -qF "logging in again" <<<"$line")"
check "the remote no longer lists the plugin's device" false "$(device_listed "$id")"
check "the old token is refused on the remote" 401 "$(token_works "$token")"
clean_log "$since_log" "$token"

exit $FAILED
