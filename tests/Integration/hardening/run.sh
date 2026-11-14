#!/usr/bin/env bash
# The plugin on a real server against a remote that misbehaves on purpose (hostile.py): huge bodies, redirects to another
# host, stalled bodies, poisoned URLs. The proof in every scenario is what arrived at the third host (nothing), what the
# fake sent, the memory of a container capped at 1500 MB, and the log at its most verbose.
# Takes about 6 minutes, because it waits for real timeouts. Containers and volumes are removed at the end.
# usage: run.sh   (scratch goes in WORK, by default ${TMPDIR:-/tmp}/jm-hard-run; NAME, PORT, FAKE and THIRD move it)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/jm-hard-run}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
NAME=${NAME:-jm-hard-a}
PORT=${PORT:-18458}
FAKE=${FAKE:-18455}
THIRD=${THIRD:-18456}
REMOTE_PASSWORD=hostile-password-sentinel
REMOTE_TOKEN=hostile-remote-token-sentinel
FAKE_URL=http://host.docker.internal:$FAKE
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshHard", Device="hard", DeviceId="jm-hard-admin", Version="0.1"'
U=http://127.0.0.1:$PORT
PLUGIN=/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration
PROFILE_DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mp4,mkv","VideoCodec":"h264","AudioCodec":"aac"}],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
FAILED=0
mkdir -p "$WORK"
rm -rf "$WORK"
mkdir -p "$WORK"

req() {
  local method=$1 path=$2 body=${3:-} token
  token=$(cat "$WORK/token" 2>/dev/null || true)
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$token\"" \
    -H 'Content-Type: application/json' ${body:+-d "$body"} "$U$path" >"$WORK/status"
  cat "$WORK/body"
}
get() { req GET "$1"; }
post() { req POST "$1" "${2:-}"; }
status() { cat "$WORK/status"; }
check() { if [ "$2" = "$3" ]; then echo "PASS $1: $3"; else echo "FAIL $1: expected $2, got $3"; FAILED=1; fi; }
at_most() { if [ "$3" -le "$2" ]; then echo "PASS $1: $3 (at most $2)"; else echo "FAIL $1: $3, more than $2"; FAILED=1; fi; }
set_mode() { echo "$1" >"$WORK/mode"; }
third_hits() { [ -f "$WORK/third.log" ] && wc -l <"$WORK/third.log" | tr -d ' ' || echo 0; }
logins() { grep -c '^POST /Users/AuthenticateByName' "$WORK/hostile.log"; }
memory_mb() { docker stats --no-stream --format '{{.MemUsage}}' "$NAME" | awk '{ v = $1 + 0; if ($1 ~ /GiB/) v *= 1024; printf "%d\n", v }'; }
alive() { [ "$(docker inspect -f '{{.State.Running}}' "$NAME")" = true ] && curl -fsS -o /dev/null "$U/System/Info/Public" && echo yes || echo no; }

wait_ready() {
  local i
  for i in $(seq 1 120); do
    curl -fsS "$U/System/Info/Public" 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "server is not ready" >&2
  exit 1
}

login() {
  rm -f "$WORK/token"
  post /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken >"$WORK/token.new"
  mv "$WORK/token.new" "$WORK/token"
}

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" >/dev/null 2>&1 || true
  [ -n "${HOSTILE_PID:-}" ] && kill "$HOSTILE_PID" 2>/dev/null
  true
}
trap cleanup EXIT

sync_now() {
  local i started=$SECONDS
  post /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 240); do
    [ "$(get /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && { echo $((SECONDS - started)) >"$WORK/sync-seconds"; return 0; }
    sleep 1
  done
  echo "sync did not end" >&2
  return 1
}
last_failure() { get /JellyMesh/Admin/Sync | jq -r '.Servers[0].Failure'; }

scan() {
  local last new i
  last=$(get /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
  post /Library/Refresh >/dev/null
  for i in $(seq 1 120); do
    new=$(get /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && { sleep 2; return 0; }
    sleep 1
  done
}

client() { # client <device>: a token for a device, because a playback belongs to one.
  [ -f "$WORK/client-$1" ] || curl -sS -X POST -H "$ACCEPT" -H "Authorization: MediaBrowser Client=\"Hard\", Device=\"$1\", DeviceId=\"$1\", Version=\"0.1\"" \
    -H 'Content-Type: application/json' -d '{"Username":"admin","Pw":"jellymesh-test"}' "$U/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/client-$1"
  cat "$WORK/client-$1"
}
open() { # open <device> <item>: PlaybackInfo. Prints the status; the answer is in $WORK/open-<device>.
  local device=$1 item=$2
  curl -sS -o "$WORK/open-$device" -w '%{http_code}' --max-time 60 -X POST -H "$ACCEPT" \
    -H "Authorization: MediaBrowser Client=\"Hard\", Device=\"$device\", DeviceId=\"$device\", Version=\"0.1\", Token=\"$(client "$device")\"" \
    -H 'Content-Type: application/json' -d "$PROFILE_DIRECT" "$U/Items/$item/PlaybackInfo"
}
url_of() { echo "$U$(jq -r '.MediaSources[0].TranscodingUrl' "$WORK/open-$1")"; }
close() { # close <device>
  local session
  session=$(jq -r '.PlaySessionId // empty' "$WORK/open-$1" 2>/dev/null)
  [ -n "$session" ] && curl -sS -o /dev/null -X DELETE -H "Authorization: MediaBrowser Client=\"Hard\", Device=\"$1\", DeviceId=\"$1\", Version=\"0.1\", Token=\"$(client "$1")\"" "$U/Videos/ActiveEncodings?deviceId=$1&playSessionId=$session"
}
clean_body() { # clean_body <what> <file>: nothing a client receives names the remote.
  local found
  found=$(grep -c -E "host\.docker\.internal|$FAKE|$THIRD|$REMOTE_TOKEN|/secret/|hostile-play" "$2")
  check "$1 names nothing of the remote" 0 "$found"
}

echo "--- server with the plugin, capped at 1500 MB, logging everything"
cleanup
python3 "$S/hostile.py" "$FAKE" "$THIRD" "$WORK" &
HOSTILE_PID=$!
docker run -d --name "$NAME" --memory 1500m -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway \
  -e TZ=Europe/Rome -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$NAME-fed:/federata" "$IMAGE" >/dev/null
wait_ready
sleep 3
post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null
get /Startup/User >/dev/null
post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
post /Startup/Complete >/dev/null
login
docker exec "$NAME" sh -c 'mkdir -p /federata/Film /federata/Serie && touch /federata/Film/.jellymesh /federata/Serie/.jellymesh'
post "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/federata/Film" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
docker exec "$NAME" sh -c 'printf "%s" "{\"Serilog\":{\"MinimumLevel\":{\"Default\":\"Verbose\",\"Override\":{\"Microsoft\":\"Verbose\",\"System\":\"Verbose\"}}}}" >/config/config/logging.json'
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$NAME"
sleep 3
wait_ready
login
cfg=$(get "$PLUGIN" | jq '.FederatedFolderPath = "/federata" | .PlaybackIdleSeconds = 30')
post "$PLUGIN" "$cfg" >/dev/null
check "plugin loaded" JellyMesh "$(get /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Name')"

echo "--- a remote that behaves, first"
set_mode normal
server=$(post /JellyMesh/Admin/Servers "$(jq -n --arg u "$FAKE_URL" --arg p "$REMOTE_PASSWORD" '{Name:"ostile",Url:$u,Username:"jm",Password:$p}')" | jq -r .Id)
check "server added" 200 "$(status)"
libraries=$(get "/JellyMesh/Admin/Servers/$server/Libraries" | jq -c '{LibraryIds: map(.Id)}')
post "/JellyMesh/Admin/Servers/$server/Libraries" "$libraries" >/dev/null
sync_now
check "sync" null "$(last_failure)"
# Stubs go to the folder of the server, which an admin adds to the library.
id=${server//-/}
folder="/federata/${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}/Film"
post "/Library/VirtualFolders/Paths?refreshLibrary=false" "$(jq -n --arg p "$folder" '{Name: "Film", PathInfo: {Path: $p}}')" >/dev/null
check "the folder of the server is linked" 204 "$(status)"
scan
film=$(get '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -r '.Items[0].Id')
check "the films are in the library" 3 "$(get '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -r .TotalRecordCount)"

set_mode direct
check "direct play opens" 200 "$(open d1 "$film")"
clean_body "the answer of a direct play" "$WORK/open-d1"
curl -sS -o "$WORK/file" -D "$WORK/file-head" "$(url_of d1)"
check "the bytes arrive whole" 1048576 "$(wc -c <"$WORK/file" | tr -d ' ')"
check "only the media headers pass" 0 "$(grep -c -i 'remote-name' "$WORK/file-head")"
close d1

set_mode normal
check "HLS opens" 200 "$(open h1 "$film")"
clean_body "the answer of an HLS play" "$WORK/open-h1"
master=$(url_of h1)
curl -sS -o "$WORK/master" "$master"
clean_body "the master playlist" "$WORK/master"
check "the master was rewritten" 1 "$(grep -c '^f/0/main.m3u8' "$WORK/master")"
close h1

echo "--- redirects to another host"
set_mode pi-redirect
check "PlaybackInfo redirected: refused" 503 "$(open r1 "$film")"
clean_body "the refusal" "$WORK/open-r1"
set_mode catalog-redirect
sync_now
check "catalog redirected: refused" RedirectRefused "$(last_failure)"
set_mode redirect-all
post "/JellyMesh/Admin/Servers/$server/Test" >/dev/null
check "a call redirected to another host is refused" RedirectRefused "$(jq -r .Failure "$WORK/body")"
clean_body "the answer to the administrator" "$WORK/body"
check "the third host saw nothing" 0 "$(third_hits)"

echo "--- huge answers"
set_mode catalog-huge
base=$(memory_mb)
sync_now
check "catalog of 2 GB: refused" ResponseTooLarge "$(last_failure)"
check "the films are still there" 3 "$(get /JellyMesh/Admin/Sync | jq -r '.Servers[0].Movies')"
check "the server is alive" yes "$(alive)"
sent=$(grep '^BYTES' "$WORK/hostile.log" | tail -1 | cut -d' ' -f2)
at_most "bytes of one huge answer accepted" $((64 * 1024 * 1024)) "$sent"
echo "memory before $base MB, after $(memory_mb) MB"

set_mode pi-huge
check "PlaybackInfo of 2 GB: refused" 503 "$(open b1 "$film")"
set_mode master-huge
check "playlist of 2 GB: refused" 503 "$(open b2 "$film")"
check "the server is alive" yes "$(alive)"
at_most "memory under the hostile answers, MB" 1400 "$(memory_mb)"

set_mode normal
: >"$WORK/fourth"
set_mode image-huge
sync_now
check "catalog read with a huge image" null "$(last_failure)"
big=$(docker exec "$NAME" sh -c 'find /federata -type f -size +16M | wc -l' | tr -d ' ')
check "no huge image on disk" 0 "$big"
rm -f "$WORK/fourth"

echo "--- error bodies are not read"
set_mode pi-error
started=$SECONDS
check "a 500 with a body of 2 GB: refused" 503 "$(open e1 "$film")"
at_most "seconds to refuse it" 8 $((SECONDS - started))
clean_body "the refusal" "$WORK/open-e1"
set_mode catalog-error
sync_now
check "a catalog error with a huge body" ServerError "$(last_failure)"
at_most "seconds of that sync" 12 "$(cat "$WORK/sync-seconds")"

echo "--- bodies that stall"
set_mode catalog-stall
sync_now
check "a catalog that stalls" Timeout "$(last_failure)"
at_most "seconds of that sync" 40 "$(cat "$WORK/sync-seconds")"
set_mode pi-stall
started=$SECONDS
check "PlaybackInfo that stalls: refused" 503 "$(open s1 "$film")"
at_most "seconds to refuse it" 14 $((SECONDS - started))

set_mode direct
check "a stream opens" 200 "$(open s2 "$film")"
url=$(url_of s2)
set_mode stream-stall
started=$SECONDS
curl -sS -o "$WORK/stalled" --max-time 70 "$url" 2>"$WORK/stalled-err"
code=$?
took=$((SECONDS - started))
check "a stream that stops sending ends with a broken connection, not a hang" true "$([ "$code" != 0 ] && [ "$code" != 28 ] && echo true || echo false)"
at_most "seconds of that stream (idle limit 30)" 45 "$took"
close s2

echo "--- a remote that steers the plugin with URLs"
for mode_name in pi-abs pi-rooted pi-long pi-subtitle master-abs variant-abs master-many pi-null; do
  set_mode "$mode_name"
  check "$mode_name: refused" 503 "$(open "p-$mode_name" "$film")"
  clean_body "$mode_name refusal" "$WORK/open-p-$mode_name"
done
set_mode master-tag
check "a tag with a stray URI passes the probe" 200 "$(open t1 "$film")"
code=$(curl -sS -o "$WORK/poisoned" -w '%{http_code}' "$(url_of t1)")
check "the client never receives it" 502 "$code"
clean_body "the 502" "$WORK/poisoned"
echo "body of a 502: $(head -c 300 "$WORK/poisoned")"
close t1
check "the third host saw nothing" 0 "$(third_hits)"

set_mode stream-html
check "a stream that claims to be a page opens" 200 "$(open x1 "$film")"
curl -sS -D "$WORK/html-head" -o /dev/null "$(url_of x1)"
check "it is served as bytes" 1 "$(grep -c -i '^content-type: application/octet-stream' "$WORK/html-head")"
close x1

echo "--- a direct stream is proxied without being held in memory"
set_mode stream-big
check "a stream of 3 GB opens" 200 "$(open g1 "$film")"
base=$(memory_mb)
curl -sS -o /dev/null --max-time 120 "$(url_of g1)" &
reader=$!
top=0
while kill -0 "$reader" 2>/dev/null; do
  now=$(memory_mb)
  [ "$now" -gt "$top" ] && top=$now
  sleep 1
done
echo "memory before $base MB, highest $top MB"
at_most "memory growth while 3 GB pass, MB" 200 $((top - base))
close g1

echo "--- only a refused token costs a login"
set_mode pi-403
before=$(logins)
check "a closed route: refused" 503 "$(open l1 "$film")"
check "it cost no login" "$before" "$(logins)"
set_mode pi-401
check "a refused token: refused" 503 "$(open l2 "$film")"
check "it cost one login" $((before + 1)) "$(logins)"

echo "--- what clients can multiply"
set_mode pi-slow
for i in $(seq 1 40); do
  ( open "c$i" "$film" >"$WORK/many-$i" ) &
done
wait $(jobs -p | grep -v "^$HOSTILE_PID$") 2>/dev/null
at_most "PlaybackInfo calls at once on the remote" 16 "$(cat "$WORK/peak")"
at_most "playbacks left on the remote" 16 "$(grep -c '^DELETE /Videos/ActiveEncodings' "$WORK/hostile.log" | awk '{print 16}')"
check "the server is alive" yes "$(alive)"

set_mode direct
check "a stream to read in parallel" 200 "$(open m1 "$film")"
url=$(url_of m1)
set_mode stream-big
for i in $(seq 1 20); do
  curl -sS -o /dev/null -w '%{http_code}\n' --max-time 4 --limit-rate 100k "$url" >"$WORK/parallel-$i" 2>/dev/null &
done
sleep 6
refused=$(cat "$WORK"/parallel-* | grep -c '^503$')
check "requests over the limit of a session are refused" 12 "$refused"
close m1

echo "--- a null in a page stops only that server"
set_mode catalog-null
sync_now
check "a page with a null" InvalidResponse "$(last_failure)"
check "the server is alive" yes "$(alive)"

set_mode normal
sync_now
check "back to normal, the sync works" null "$(last_failure)"
set_mode direct
check "back to normal, playback works" 200 "$(open z1 "$film")"
close z1
check "the server never restarted" 0 "$(docker inspect -f '{{.RestartCount}}' "$NAME")"
check "never killed for memory" false "$(docker inspect -f '{{.State.OOMKilled}}' "$NAME")"

echo "--- the log, at the most verbose level"
docker exec "$NAME" sh -c 'cat /config/log/*.log /var/log/jellyfin/*.log 2>/dev/null' >"$WORK/server.log"
docker logs "$NAME" >>"$WORK/server.log" 2>&1
wc -l <"$WORK/server.log"
check "the log is verbose" true "$([ "$(grep -c -E '\[(VRB|DBG)\]' "$WORK/server.log")" -gt 100 ] && echo true || echo false)"
echo "verbose lines: $(grep -c -E '\[(VRB|DBG)\]' "$WORK/server.log")"
KEY=$(docker exec "$NAME" sh -c 'cat "$(find /config -name credential.key | head -1)"')
check "the credential key file exists" 64 "${#KEY}"
for secret in "$REMOTE_TOKEN" "$REMOTE_PASSWORD" "$KEY" 'host.docker.internal' "$FAKE" "$THIRD" 'MediaBrowser Client="JellyMesh"'; do
  check "the log holds no '$secret'" 0 "$(grep -c -F "$secret" "$WORK/server.log")"
done
grep -F -e "$FAKE" -e 'host.docker.internal' "$WORK/server.log" | head -5
check "plugin configuration holds no token or password" 0 "$(docker exec "$NAME" sh -c 'cat /config/plugins/configurations/*JellyMesh*.xml' | grep -c -F -e "$REMOTE_TOKEN" -e "$REMOTE_PASSWORD")"

exit "$FAILED"
