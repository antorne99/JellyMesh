#!/usr/bin/env bash
# Measures a consumer's first sync and materialization against stock remotes with a large synthetic catalog
# (fake_remote.py, no media), then an incremental sync with no changes, a full sync with no changes and the nightly
# reconciliation, which also checks every stub and links the versions again.
# One Jellyfin 12.4 with the plugin, the fakes on the host. Containers and volumes are removed at the end unless KEEP=1.
# SCALE_REMOTES fakes (default 1) serve the same catalog, each as its own server, so every title has one stub in each
# server folder and the stubs form one version group. The catalog size and images come from the SCALE_* variables of
# fake_remote.py; SCALE_SCAN=1 also links the server folders to two libraries afterwards, scans them, and repeats the
# incremental sync three times (the first finds the scanned library, the next ones find nothing changed), the nightly
# reconciliation on scanned items and one more incremental after it.
# SCALE_NAME, SCALE_PORT and SCALE_FAKE_PORT (the first fake; the others follow) override the container and the ports.
# usage: run.sh   (raw data and results.txt go in WORK, by default ${TMPDIR:-/tmp}/jm-scale-run)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/jm-scale-run}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
NAME=${SCALE_NAME:-jm-scale-consumer}
PORT=${SCALE_PORT:-18460}
FAKE=${SCALE_FAKE_PORT:-18461}
REMOTES=${SCALE_REMOTES:-1}
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
  [ -n "${FAKE_PIDS:-}" ] && kill $FAKE_PIDS 2>/dev/null
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
  in_box "grep -o '\"LocalItemIds\"' \$(find /config -name federated-stubs.json) | wc -l" >"$WORK/$phase-groups.txt" 2>/dev/null
  # After the scan: the films and episodes Jellyfin lists, by how many versions each one holds.
  [ -f "$WORK/scan-requested" ] && get '/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=MediaSourceCount' |
    jq -c '[.Items[] | .MediaSourceCount // 0] | group_by(.) | map({versions: .[0], items: length})' >"$WORK/$phase-links.txt"
  in_box "find /federata -type f | sed -e 's/.*\.//' | sort | uniq -c; echo total \$(find /federata -type f | wc -l); echo kib \$(du -sk /federata | cut -f1)" >"$WORK/$phase-tree.txt"
}

# Waits until no scheduled task runs, such as the post-scan task a sync queued, so that a phase measures its sync alone.
settle() {
  local i
  for i in $(seq 1 3600); do
    [ "$(get /ScheduledTasks | jq '[.[] | select(.State != "Idle")] | length')" = 0 ] && return
    sleep 2
  done
}

# Runs one sync and waits for it: <phase> <start command...>.
measure() {
  local phase=$1 before i
  shift
  settle
  # Other containers on the same Docker VM skew the times: their load is kept with the phase.
  docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' >"$WORK/$phase-load.txt" 2>/dev/null
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
start_nightly() { post "/ScheduledTasks/Running/$NIGHTLY_TASK"; }

# The fakes share one request log: each line is one short append.
FAKE_PIDS=
for n in $(seq 1 "$REMOTES"); do
  echo "--- fake remote $n on :$((FAKE + n - 1))"
  SCALE_SERVER_ID=$(printf '%032x' $((0x8000 + n))) SCALE_SERVER_NAME="scale-remote-$n" \
    python3 "$S/fake_remote.py" "$((FAKE + n - 1))" "$WORK/requests.log" >"$WORK/fake-$n.out" 2>&1 &
  FAKE_PIDS="$FAKE_PIDS $!"
done

echo "--- consumer $NAME on :$PORT"
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" --add-host host.docker.internal:host-gateway \
  -e TZ=UTC -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$NAME-fed:/federata" "$IMAGE" >/dev/null
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
NIGHTLY_TASK=$(get /ScheduledTasks | jq -r '.[] | select(.Key == "JellyMeshReconciliation") | .Id')

SERVERS=
for n in $(seq 1 "$REMOTES"); do
  name=scale; [ "$REMOTES" -gt 1 ] && name="scale-$n"
  server=$(post /JellyMesh/Admin/Servers "$(jq -n --arg u "http://host.docker.internal:$((FAKE + n - 1))" --arg n "$name" '{Name:$n,Url:$u,Username:"jellymesh",Password:"jellymesh-test"}')" | jq -r .Id)
  [ -n "$server" ] && [ "$server" != null ] || { echo "cannot add server $name" >&2; exit 1; }
  # The server folder is named by the dashed form of the GUID.
  server=$(echo "$server" | sed -E 's/^(.{8})-?(.{4})-?(.{4})-?(.{4})-?(.{12})$/\1-\2-\3-\4-\5/')
  libraries=$(get "/JellyMesh/Admin/Servers/$server/Libraries" | jq -c '{LibraryIds: map(.Id)}')
  post "/JellyMesh/Admin/Servers/$server/Libraries" "$libraries" >/dev/null
  echo "server $name $server, libraries $libraries"
  SERVERS="$SERVERS $server"
done

sample &
SAMPLER_PID=$!
sleep 5
snapshot idle

measure first start_full
measure incremental start_incremental
measure repeat start_full
measure nightly start_nightly

if [ "${SCALE_SCAN:-0}" = 1 ]; then
  echo "--- linking the server folders and scanning"
  films=; shows=
  for server in $SERVERS; do
    films="$films&paths=/federata/$server/Film"
    shows="$shows&paths=/federata/$server/Serie"
  done
  post "/Library/VirtualFolders?name=Films&collectionType=movies&refreshLibrary=false$films" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
  post "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=false$shows" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
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
  measure incremental-scanned-again start_incremental
  measure incremental-scanned-third start_incremental
  measure nightly-scanned start_nightly
  measure incremental-after-nightly start_incremental
fi

sleep 3
kill "$SAMPLER_PID" 2>/dev/null
in_box 'cat /config/log/log_*.log' >"$WORK/jellyfin.log"
python3 "$S/analyze.py" "$WORK" | tee "$WORK/results.txt"
