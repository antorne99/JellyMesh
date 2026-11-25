#!/usr/bin/env bash
# Integration 5 of the 0.6 spec (F34, §10 rows 432-434): the "Modifica" form of a server, end to end.
# A new address of the same server keeps the login; another server's address is refused; a new user ends the streams and
# the old login within 3 s; a library the new user does not see leaves at the sync; with the remote off only the name saves.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-edit] [PORT=18490] edit.sh   (KEEP=1 leaves the containers up)
set -euo pipefail
PREFIX=${PREFIX:-jm-edit}
PORT=${PORT:-18490}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

port_of() { case $1 in local) echo "$PORT" ;; remote) echo $((PORT + 1)) ;; other) echo $((PORT + 2)) ;; esac; }
name_of() { echo "$PREFIX-$1"; }
down() {
  docker rm -f "$PREFIX-local" "$PREFIX-remote" "$PREFIX-other" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" "$PREFIX-local-media" \
    "$PREFIX-remote-config" "$PREFIX-remote-cache" "$PREFIX-remote-fed" \
    "$PREFIX-other-config" "$PREFIX-other-cache" "$PREFIX-other-fed" >/dev/null 2>&1 || true
}
[ "${KEEP:-}" = 1 ] || trap down EXIT

PROFILE_DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
FILM="JM Confronto Risoluzione"
SIZE=$(wc -c <"$(find "$MEDIA/remoto-standard/film/$FILM (2026)" -name '*.mkv' | head -1)" | tr -d ' ')
now() { perl -MTime::HiRes=time -e 'printf "%.2f\n", time'; }
since() { perl -e "printf '%.1f', $(now) - $1"; }
under() { perl -e "exit !($1 < $2)" && echo yes || echo "no ($1)"; }
yes_no() { "$@" && echo true || echo false; }
stamp() { date -u +%Y-%m-%dT%H:%M:%SZ; }

# remote_db <sql>: a statement on a copy of the remote's database.
remote_db() {
  local db="$WORK/remote-db"
  rm -rf "$db" && mkdir -p "$db"
  docker exec "$PREFIX-remote" sh -c 'cd /config/data && ls jellyfin.db*' | while read -r f; do docker cp "$PREFIX-remote:/config/data/$f" "$db/$f" >/dev/null; done
  sqlite3 "$db/jellyfin.db" "$1"
}
remote_token() { remote_db "select AccessToken from Devices where DeviceId = 'jellymesh-${1//-/}' order by Id desc limit 1;"; }
# device_users <server id>: the remote users that have a login on the plugin's device for that server.
device_users() {
  remote_db "select u.Username from Devices d join Users u on u.Id = d.UserId where d.DeviceId = 'jellymesh-${1//-/}' order by u.Username;" | paste -sd, -
}
token_works() { curl -s -o /dev/null -w '%{http_code}' -H "Authorization: MediaBrowser Token=\"$1\"" "http://127.0.0.1:$(port_of remote)/System/Info"; }
# clean_log <since> <token>...: the log holds no token, and a remote address only in the whole error of a plugin warning or
# error (row 550); the activity log holds no remote address.
clean_log() {
  local log t address="host\.docker\.internal|$REMOTE_IP|:$(port_of remote)|:$(port_of other)"
  log=$(docker logs --since "$1" "$PREFIX-local" 2>&1)
  shift
  for t in "$@"; do check "the log holds no token" 0 "$(grep -cF "$t" <<<"$log" || true)"; done
  check "the log has a remote address only in the errors of the plugin" 0 "$(A=$address awk '
    /^\[/ { error = ($0 ~ /\] \[(WRN|ERR)\] \[[0-9]+\] Jellyfin\.Plugin\.JellyMesh\./) }
    $0 ~ ENVIRON["A"] && !error { n++ } END { print n + 0 }' <<<"$log")"
  check "the activity log holds no remote address" 0 "$(get local "/System/ActivityLog/Entries?minDate=$1" | grep -cE "$address" || true)"
}
log_line() { docker logs --since "$1" "$PREFIX-local" 2>&1 | { grep -iF "$2" || true; } | tail -1 | sed -E 's/^.*\] //'; }
# edit <method> <path> <name> <url> <user> [password] [library ids json]: the form, as the page sends it. Prints the body.
edit() {
  local body
  body=$(jq -n --arg n "$3" --arg u "$4" --arg x "$5" --arg p "${6:-}" --argjson l "${7:-null}" \
    '{Name:$n, Url:$u, Username:$x} + (if $p == "" then {} else {Password:$p} end) + (if $l == null then {} else {LibraryIds:$l} end)')
  req local "$1" "$2" "$body"
}
save() { edit PUT "/JellyMesh/Admin/Servers/$ID" "$@"; }
verify() { edit POST "/JellyMesh/Admin/Servers/$ID/Edit/Check" "$@"; }
imported() { get local /JellyMesh/Admin/Status | jq -c --arg id "$ID" '[.Servers[] | select(.Id == $id) | .ImportedLibraryIds[]] | sort'; }
strms() { docker exec "$PREFIX-local" sh -c "find '$(server_folder "$ID")' -path '*/$1/*' -name '*.strm' 2>/dev/null | wc -l" | tr -d ' '; }
open() { post local "/Items/$1/PlaybackInfo" "$2"; }
url_in() { echo "http://127.0.0.1:8096$(jq -r '.MediaSources[0].TranscodingUrl' <<<"$1")"; }
inside() { docker exec "$PREFIX-local" curl "$@"; }
slow_read() { inside -s -o /dev/null --limit-rate 200k -m 120 -w '%{http_code} %{size_download} exit %{exitcode}' "$1"; }
reading() { kill -0 "$1" 2>/dev/null; }
# only <user> <library>...: the remote user sees those libraries alone.
only() {
  local user=$1 libs uid policy
  shift
  libs=$(get remote /Library/VirtualFolders | jq -c --args '[.[] | select(.Name | IN($ARGS.positional[])) | .ItemId]' "$@")
  uid=$(user_id remote "$user")
  policy=$(get remote "/Users/$uid" | jq -c --argjson f "$libs" '.Policy | .EnableAllFolders = false | .EnabledFolders = $f')
  post remote "/Users/$uid/Policy" "$policy" >/dev/null
}

down
mkdir -p "$WORK"
for s in remote other; do
  start "$s" "$MEDIA/remoto-standard:/media:ro"
  library "$s" Film movies /media/film
  library "$s" Serie tvshows /media/serie
  user "$s" jellymesh >/dev/null
  scan "$s"
done
user remote jellymesh2 >/dev/null
only jellymesh2 Film
REMOTE_IP=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$PREFIX-remote")

start local "$PREFIX-local-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film /media/serie && touch /media/film/.keep /media/serie/.keep'
library local Film movies /media/film
library local Serie tvshows /media/serie
bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
# Small socket buffers in the local's network, so a slow reader stops soon after its stream ends.
docker run --rm --name "$PREFIX-sysctl" --privileged --network "container:$PREFIX-local" --entrypoint sh "$IMAGE" -c \
  'echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_rmem && echo "4096 16384 65536" >/proc/sys/net/ipv4/tcp_wmem'
login local
login remote
settings local '.FederatedFolderPath = "/federata"' >/dev/null
URL="http://host.docker.internal:$(port_of remote)"
ID=$(add_remote local Casa "$URL" jellymesh)
import_all local "$ID"
sync_now local
# Each remote library has its own folder in the server folder, with the kinds it holds.
for kind in Film Serie; do
  docker exec "$PREFIX-local" sh -c "find '$(server_folder "$ID")' -mindepth 2 -maxdepth 2 -type d -name $kind" |
    while read -r f; do link_folder local "$kind" "$f" >/dev/null; done
done
scan local
check "both libraries are imported" 2 "$(imported | jq length)"
BOTH=$(imported)
check "the films are federated" true "$(yes_no [ "$(strms Film)" -gt 0 ])"
check "the series are federated" true "$(yes_no [ "$(strms Serie)" -gt 0 ])"
since_log=$(stamp)

echo "== a new address of the same server keeps the live login"
token=$(remote_token "$ID")
NEW_URL="http://$REMOTE_IP:8096"
verify Casa "$NEW_URL" jellymesh >/dev/null
check "the check accepts the new address" 200 "$(status)"
save Casa "$NEW_URL" jellymesh >/dev/null
check "the save answers" 200 "$(status)"
check "the remote keeps the same login" "$token" "$(remote_token "$ID")"
check "the login still works" 200 "$(token_works "$token")"
sync_now local
item=$(stub_item local "$FILM")
check "a title still opens" 200 "$(open "$item" "$PROFILE_DIRECT" >/dev/null; status)"
URL=$NEW_URL

echo "== the address of another server is refused"
other="http://host.docker.internal:$(port_of other)"
body=$(verify Casa "$other" jellymesh)
check "the check refuses it" 409 "$(status)"
check "with the reason" OtherServer "$(jq -r .Failure <<<"$body")"
echo "    message: $(jq -r .Message <<<"$body")"
save Casa "$other" jellymesh >/dev/null
check "the save refuses it" 409 "$(status)"
check "the address is unchanged" "$URL" "$(get local /JellyMesh/Admin/Status | jq -r --arg id "$ID" '.Servers[] | select(.Id == $id) | .Url')"
check "the other server keeps no login of the plugin" 0 "$(curl -s -H "Authorization: MediaBrowser Token=\"$(token_of other)\"" \
  "http://127.0.0.1:$(port_of other)/Devices" | jq --arg admin "$PREFIX-admin" '[.Items[] | select((.Id | startswith("jellymesh-")) and .Id != $admin)] | length')"

echo "== a new user ends the streams and the old login within 3 s"
login local
item=$(stub_item local "$FILM")
proxy=$(url_in "$(open "$item" "$PROFILE_DIRECT")")
slow_read "$proxy" >"$WORK/read-proxy" 2>/dev/null &
reader=$!
sleep 4
check "the proxied stream is reading" true "$(yes_no reading $reader)"
old_token=$(remote_token "$ID")
check "the old user holds the plugin's device" jellymesh "$(device_users "$ID")"
body=$(verify Casa "$URL" jellymesh2 jellymesh-test)
check "the check accepts the new user" 200 "$(status)"
echo "    consequences: $(jq -c .Consequences <<<"$body")"
t=$(now)
save Casa "$URL" jellymesh2 jellymesh-test >/dev/null
saved=$(since "$t")
check "the save answers" 200 "$(status)"
wait $reader || true
ended=$(since "$t")
echo "    the save took $saved s; the stream ended after $ended s ($(cat "$WORK/read-proxy")), of $SIZE bytes"
check "the stream ends within 3 s" yes "$(under "$ended" 3)"
check "the stream did not finish" true "$(yes_no [ "$(cut -d' ' -f2 "$WORK/read-proxy")" -lt "$SIZE" ])"
check "the old login is refused on the remote" 401 "$(token_works "$old_token")"
check "only the new user holds the plugin's device" jellymesh2 "$(device_users "$ID")"
check "the new login works" 200 "$(token_works "$(remote_token "$ID")")"
echo "    log: $(log_line "$since_log" "old user")"

echo "== the library the new user does not see leaves at the sync"
check "only Film stays imported" 1 "$(imported | jq length)"
for _ in $(seq 1 120); do [ "$(get local /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && break; sleep 1; done
sync_now local
check "the films stay" true "$(yes_no [ "$(strms Film)" -gt 0 ])"
check "the series are gone" 0 "$(strms Serie)"
login local
item=$(stub_item local "$FILM")
check "a title opens with the new user" 200 "$(open "$item" "$PROFILE_DIRECT" >/dev/null; status)"

echo "== with the remote off only the name saves"
docker stop "$PREFIX-remote" >/dev/null
lib=$(imported)
body=$(verify Mare "$URL" jellymesh2)
check "the check of a new name succeeds" 200 "$(status)"
check "and says the remote is off" true "$(jq -r .RemoteOffline <<<"$body")"
save Mare "$URL" jellymesh2 >/dev/null
check "the new name saves" 200 "$(status)"
check "the name is saved" Mare "$(get local /JellyMesh/Admin/Status | jq -r --arg id "$ID" '.Servers[] | select(.Id == $id) | .Name')"
save Mare "http://host.docker.internal:$(port_of remote)" jellymesh2 >/dev/null
check "a new address is refused" 502 "$(status)"
save Mare "$URL" jellymesh jellymesh-test >/dev/null
check "a new user is refused" 502 "$(status)"
save Mare "$URL" jellymesh2 another-password >/dev/null
check "a new password is refused" 502 "$(status)"
save Mare "$URL" jellymesh2 "" "$BOTH" >/dev/null
check "new libraries are refused" 502 "$(status)"
check "the libraries are unchanged" "$lib" "$(imported)"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
clean_log "$since_log" "$token" "$old_token"

echo "=== failed: $FAILED"
exit "$FAILED"
