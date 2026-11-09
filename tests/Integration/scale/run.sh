#!/usr/bin/env bash
# Measures a consumer's first sync and materialization against a stock remote with a large synthetic catalog
# (fake_remote.py, no media), then an incremental sync with no changes and a full sync with no changes.
# One Jellyfin 12.4 with the plugin, the fake on the host. Containers and volumes are removed at the end unless KEEP=1.
# The catalog size and images come from the SCALE_* variables of fake_remote.py; SCALE_SCAN=1 also links the federated
# folder to two libraries afterwards, scans them, and repeats the incremental sync on scanned items.
# usage: run.sh   (raw data and results.txt go in WORK, by default ${TMPDIR:-/tmp}/jm-scale-run)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/jm-scale-run}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
NAME=jm-scale-consumer
PORT=18460
FAKE=18461
KEY=jm-scale-test-key-0123456789abcdef0123456789
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshScale", Device="scale", DeviceId="jm-scale-admin", Version="0.1"'
U=http://127.0.0.1:$PORT
PLUGIN=/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration
rm -rf "$WORK"
mkdir -p "$WORK"

req() {
  local method=$1 path=$2 body=${3:-} token
  token=$(cat "$WORK/token" 2>/dev/null || true)
  curl -sS -X "$method" -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$token\"" \
    -H 'Content-Type: application/json' ${body:+-d "$body"} "$U$path"
}
get() { req GET "$1"; }
post() { req POST "$1" "${2:-}"; }
in_box() { docker exec "$NAME" sh -c "$1"; }
now_ms() { python3 -c 'import time; print(int(time.time() * 1000))'; }

wait_ready() {
  local i
  for i in $(seq 1 180); do
    curl -fsS "$U/System/Info/Public" 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return 0
    sleep 1
  done
  echo "server is not ready" >&2
  exit 1
}

login() {
  post /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken >"$WORK/token.new"
  mv "$WORK/token.new" "$WORK/token"
}

cleanup() {
  [ -n "${SAMPLER_PID:-}" ] && kill "$SAMPLER_PID" 2>/dev/null
  [ -n "${FAKE_PID:-}" ] && kill "$FAKE_PID" 2>/dev/null
  if [ "${KEEP:-0}" != 1 ]; then
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" >/dev/null 2>&1 || true
  fi
  true
}
trap cleanup EXIT

# Every second: memory as docker stats reports it, CPU, and the resident and peak resident set of the Jellyfin process.
sample() {
  local stats rss
  while :; do
    stats=$(docker stats --no-stream --format '{{.MemUsage}}|{{.CPUPerc}}' "$NAME" 2>/dev/null | awk -F'|' '{ split($1, m, " "); v = m[1] + 0; if (m[1] ~ /GiB/) v *= 1024; if (m[1] ~ /KiB/) v /= 1024; printf "%d %s", v, $2 }')
    rss=$(docker exec "$NAME" sh -c "awk '/VmRSS|VmHWM/ { printf \"%d \", \$2 / 1024 }' /proc/$JPID/status" 2>/dev/null)
    echo "$(now_ms) $stats $rss" >>"$WORK/samples.txt"
    sleep 1
  done
}

# Records what a phase left behind: <phase>.
snapshot() {
  local phase=$1
  get /JellyMesh/Admin/Sync >"$WORK/$phase-status.json"
  in_box "awk '/VmHWM|VmRSS/ { print \$1, int(\$2 / 1024) }' /proc/$JPID/status; echo cgroup-peak \$(( \$(cat /sys/fs/cgroup/memory.peak) / 1048576 ))" >"$WORK/$phase-memory.txt"
  in_box "for f in \$(find /config -name federation-index.json -o -name federated-stubs.json); do echo \$(basename \$f) \$(stat -c '%s %.3Y' \$f); done" >"$WORK/$phase-files.txt"
  in_box "find /federata -type f | sed -e 's/.*\.//' | sort | uniq -c; echo total \$(find /federata -type f | wc -l); echo kib \$(du -sk /federata | cut -f1)" >"$WORK/$phase-tree.txt"
}

# Runs one sync and waits for it: <phase> <start command...>.
measure() {
  local phase=$1 before i
  shift
  before=$(get /JellyMesh/Admin/Sync | jq -r '.LastStartedUtc // ""')
  echo "$(now_ms)" >"$WORK/$phase-requested"
  "$@" >/dev/null
  for i in $(seq 1 14400); do
    sleep 2
    get /JellyMesh/Admin/Sync >"$WORK/poll.json" 2>/dev/null || continue
    [ "$(jq -r .IsRunning "$WORK/poll.json")" = false ] && [ "$(jq -r '.LastStartedUtc // ""' "$WORK/poll.json")" != "$before" ] && break
  done
  echo "$(now_ms)" >"$WORK/$phase-seen"
  snapshot "$phase"
  echo "--- $phase done: $(jq -c '{LastStartedUtc, LastFinishedUtc, Servers: [.Servers[] | {State, Failure, Movies, Series, Episodes}]}' "$WORK/$phase-status.json")"
}

start_full() { post /JellyMesh/Admin/Sync; }
start_incremental() { post "/ScheduledTasks/Running/$INCREMENTAL_TASK"; }

echo "--- fake remote on :$FAKE"
python3 "$S/fake_remote.py" "$FAKE" "$WORK/requests.log" >"$WORK/fake.out" 2>&1 &
FAKE_PID=$!

echo "--- consumer $NAME on :$PORT"
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway \
  -e TZ=UTC -e "JELLYMESH_SECRET_KEY=$KEY" -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$NAME-fed:/federata" "$IMAGE" >/dev/null
wait_ready
sleep 3
post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
get /Startup/User >/dev/null
post /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
post /Startup/Complete >/dev/null
login
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$NAME" || exit 1
sleep 3
wait_ready
login
JPID=$(in_box 'for p in /proc/[0-9]*; do tr "\0" " " <$p/cmdline 2>/dev/null | grep -q "jellyfin.dll\|/jellyfin/jellyfin" && { echo ${p#/proc/}; break; }; done')
echo "Jellyfin process: $JPID"
# No scheduled sync during the measurement: every sync here is started by hand.
cfg=$(get "$PLUGIN" | jq '.FederatedFolderPath = "/federata" | .SyncIntervalSeconds = 86400')
post "$PLUGIN" "$cfg" >/dev/null
[ "$(get /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Status')" = Active ] || { echo "plugin not active" >&2; exit 1; }
INCREMENTAL_TASK=$(get /ScheduledTasks | jq -r '.[] | select(.Key == "JellyMeshIncrementalSync") | .Id')

server=$(post /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$FAKE" '{Name:"scale",Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" | jq -r .Id)
libraries=$(get "/JellyMesh/Admin/Servers/$server/Libraries" | jq -c '{LibraryIds: map(.Id)}')
post "/JellyMesh/Admin/Servers/$server/Libraries" "$libraries" >/dev/null
echo "server $server, libraries $libraries"

sample &
SAMPLER_PID=$!
sleep 5
snapshot idle

measure first start_full
measure incremental start_incremental
measure repeat start_full

if [ "${SCALE_SCAN:-0}" = 1 ]; then
  echo "--- linking the federated folder and scanning"
  post "/Library/VirtualFolders?name=Films&collectionType=movies&refreshLibrary=false&paths=/federata/Film" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
  post "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=false&paths=/federata/Serie" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
  echo "$(now_ms)" >"$WORK/scan-requested"
  post /Library/Refresh >/dev/null
  sleep 10
  for i in $(seq 1 7200); do
    [ "$(get /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .State')" = Idle ] && break
    sleep 2
  done
  echo "$(now_ms)" >"$WORK/scan-seen"
  snapshot scan
  get '/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Series&Limit=0' | jq -r .TotalRecordCount >"$WORK/scan-items.txt"
  echo "--- scan done: $(cat "$WORK/scan-items.txt") items"
  measure incremental-scanned start_incremental
fi

sleep 3
kill "$SAMPLER_PID" 2>/dev/null
in_box 'cat /config/log/log_*.log' >"$WORK/jellyfin.log"
python3 "$S/analyze.py" "$WORK" | tee "$WORK/results.txt"
