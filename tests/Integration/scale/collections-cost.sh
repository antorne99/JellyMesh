#!/usr/bin/env bash
# Row 232: the cost of the provider collections route, whose every page rebuilds the member map of all collections.
# One Jellyfin 12.4 with the plugin is the provider: COL_FILMS films (hard links of one tiny video, so they are real
# probed films), collections of 2-20 films, most small, and a dedicated user granted the film library.
# Times Provider/Collections and Provider/Items as that user: first, middle, last page and limit=1 (one cold call, then
# COL_REPEATS), full walks of 500, concurrent walks, with the CPU-seconds of the container and the resident memory.
# COL_CHECKPOINTS (default "250 1000"): collection counts at which the measurements run, the last being the total.
# COL_NAME, COL_PORT override the container and the port. Everything is removed at the end unless KEEP=1.
# usage: WORK=<scratch> collections-cost.sh   (results-collections.txt goes in WORK)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
WORK=${WORK:-${TMPDIR:-/tmp}/jm-colcost-run}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
NAME=${COL_NAME:-jm-colcost-provider}
PORT=${COL_PORT:-18490}
FILMS=${COL_FILMS:-10000}
CHECKPOINTS=${COL_CHECKPOINTS:-"250 1000"}
COLLECTIONS=${CHECKPOINTS##* }
REPEATS=${COL_REPEATS:-5}
PAGE=500
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshColCost", Device="colcost", DeviceId="jm-colcost-admin", Version="0.1"'
PEER_AUTH='MediaBrowser Client="JellyMeshColCost", Device="peer", DeviceId="jm-colcost-peer", Version="0.1"'
U=http://127.0.0.1:$PORT
PLUGIN=/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration
OUT=$WORK/results-collections.txt
rm -rf "$WORK"
mkdir -p "$WORK"

req() {
  local method=$1 path=$2 body=${3:-}
  curl -sS -X "$method" -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$(cat "$WORK/token" 2>/dev/null)\"" \
    -H 'Content-Type: application/json' ${body:+-d "$body"} "$U$path"
}
get() { req GET "$1"; }
post() { req POST "$1" "${2:-}"; }
in_box() { docker exec "$NAME" sh -c "$1"; }
say() { echo "$*" | tee -a "$OUT"; }

cleanup() {
  if [ "${KEEP:-0}" != 1 ]; then
    docker rm -f "$NAME" >/dev/null 2>&1 || true
    docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" "$NAME-media" >/dev/null 2>&1 || true
  fi
  true
}
trap cleanup EXIT

wait_ready() {
  local i
  for i in $(seq 1 300); do
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
peer_login() {
  curl -sS -X POST -H "$ACCEPT" -H "Authorization: $PEER_AUTH" -H 'Content-Type: application/json' \
    -d '{"Username":"peer","Pw":"jellymesh-test"}' "$U/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/peer-token"
}
# Waits until no scheduled task runs, so that nothing else loads the server while a route is timed.
settle() {
  local i
  for i in $(seq 1 7200); do
    [ "$(get /ScheduledTasks | jq '[.[] | select(.State != "Idle")] | length')" = 0 ] && return
    sleep 2
  done
}
jpid() { in_box 'for p in /proc/[0-9]*; do tr "\0" " " <$p/cmdline 2>/dev/null | grep -q "jellyfin.dll\|/jellyfin/jellyfin" && { echo ${p#/proc/}; break; }; done'; }
cpu_usec() { in_box 'sed -n "s/^usage_usec //p" /sys/fs/cgroup/cpu.stat'; }
rss_mib() { in_box "awk '/VmRSS/ { print int(\$2 / 1024) }' /proc/$JPID/status"; }

# peer <path>: one request as the peer. Prints "status seconds bytes"; the body is in $WORK/peer-body.
peer() {
  curl -sS -o "$WORK/peer-body" -w '%{http_code} %{time_total} %{size_download}' -H "$ACCEPT" \
    -H "Authorization: $PEER_AUTH, Token=\"$(cat "$WORK/peer-token")\"" "$U$1"
}

# Samples the resident memory of the Jellyfin process every 0.25 s while a walk runs: start_rss / stop_rss <label>.
start_rss() {
  (in_box "rm -f /tmp/rss; echo \$\$ >/tmp/rss.pid; while :; do awk '/VmRSS/ { print int(\$2 / 1024) }' /proc/$JPID/status >>/tmp/rss; sleep 0.25; done" >/dev/null 2>&1 &)
  sleep 0.5
}
# The sampler is not a job of this shell: it ends when its loop in the container is killed.
stop_rss() {
  in_box "kill \$(cat /tmp/rss.pid) 2>/dev/null; sort -n /tmp/rss | awk 'NR == 1 { lo = \$1 } { hi = \$1 } END { printf \"rss %d-%d MiB\", lo, hi }'"
}

# stats <file of seconds>: median, min and max.
stats() { sort -n "$1" | awk '{ v[NR] = $1 } END { m = (NR % 2) ? v[(NR + 1) / 2] : (v[NR / 2] + v[NR / 2 + 1]) / 2; printf "median %.3f s (min %.3f, max %.3f, n=%d)", m, v[1], v[NR], NR }'; }

# page <label> <route> <start> <limit>: one cold call and REPEATS more; every answer must be a 200.
page() {
  local label=$1 route=$2 start=$3 limit=$4 r cold i
  r=$(peer "$route&startIndex=$start&limit=$limit")
  [ "${r%% *}" = 200 ] || { say "FAIL $label: status ${r%% *}"; FAILED=1; return; }
  cold=$(echo "$r" | cut -d' ' -f2)
  : >"$WORK/times"
  for i in $(seq 1 "$REPEATS"); do
    r=$(peer "$route&startIndex=$start&limit=$limit")
    [ "${r%% *}" = 200 ] || { say "FAIL $label: status ${r%% *}"; FAILED=1; return; }
    echo "$r" | cut -d' ' -f2 >>"$WORK/times"
  done
  say "$(printf '  %-34s first call %.3f s, then %s; %d entries, %d bytes' "$label" "$cold" "$(stats "$WORK/times")" \
    "$(jq '.Items | length' "$WORK/peer-body")" "$(echo "$r" | cut -d' ' -f3)")"
}

# walk <route>: reads every page of 500 in order, as the consumer does.
# Prints "seconds entries total pages slowest-page-s sum-of-page-s".
walk() {
  local route=$1 start=0 total=1 entries=0 pages=0 r t0 t1 slowest=0 served=0 n
  t0=$(python3 -c 'import time; print(time.time())')
  while [ "$start" -lt "$total" ]; do
    r=$(peer "$route&startIndex=$start&limit=$PAGE")
    [ "${r%% *}" = 200 ] || { echo "FAIL status ${r%% *} at $start"; return; }
    total=$(jq .TotalRecordCount "$WORK/peer-body")
    n=$(jq '.Items | length' "$WORK/peer-body")
    [ "$n" -gt 0 ] || break
    slowest=$(echo "$r" | awk -v s="$slowest" '{ print ($2 > s) ? $2 : s }')
    served=$(echo "$r" | awk -v s="$served" '{ print s + $2 }')
    entries=$((entries + n)); start=$((start + n)); pages=$((pages + 1))
  done
  t1=$(python3 -c 'import time; print(time.time())')
  echo "$(echo "$t1 - $t0" | bc) $entries $total $pages $slowest $served"
}

# measure <route name> <route> <expected total>: page times, then 3 full walks with CPU and memory.
measure() {
  local name=$1 route=$2 expected=$3 total last mid i w c0 c1
  r=$(peer "$route&startIndex=0&limit=1")
  total=$(jq .TotalRecordCount "$WORK/peer-body")
  check "$name total" "$expected" "$total"
  last=$(( (total - 1) / PAGE * PAGE ))
  mid=$(( total / 2 / PAGE * PAGE ))
  say " $name ($total films, pages of $PAGE):"
  say "$(printf '  %-34s %.3f s (status %s)' "first request of the route" "$(echo "$r" | cut -d' ' -f2)" "${r%% *}")"
  page "limit=1 at 0" "$route" 0 1
  page "first page (0)" "$route" 0 $PAGE
  page "middle page ($mid)" "$route" "$mid" $PAGE
  page "last page ($last)" "$route" "$last" $PAGE
  for i in 1 2 3; do
    start_rss
    c0=$(cpu_usec)
    w=$(walk "$route")
    c1=$(cpu_usec)
    m=$(stop_rss)
    case $w in FAIL*) say "  FAIL walk $i: $w"; FAILED=1; continue ;; esac
    set -- $w
    check "$name walk $i reads every entry once" "$3" "$2"
    say "$(printf '  full walk %d: %.2f s (pages %.2f s), %d pages, %d entries, slowest page %.3f s, CPU %.2f s, %s' "$i" "$1" "$6" "$4" "$2" "$5" \
      "$(echo "($c1 - $c0) / 1000000" | bc -l)" "$m")"
  done
}

# concurrent <route name> <route> <walkers>: that many walks at once with one peer login, standing in for several
# consumers syncing together.
concurrent() {
  local name=$1 route=$2 n=$3 i c0 c1 t0 t1 m walkers=
  rm -f "$WORK"/concurrent-*
  start_rss
  c0=$(cpu_usec)
  t0=$(python3 -c 'import time; print(time.time())')
  for i in $(seq 1 "$n"); do
    (WORK_PEER=$WORK/w$i; mkdir -p "$WORK_PEER"; cp "$WORK/peer-token" "$WORK_PEER/"; WORK=$WORK_PEER walk "$route" >"$WORK/concurrent-$i") &
    walkers="$walkers $!"
  done
  wait $walkers
  t1=$(python3 -c 'import time; print(time.time())')
  c1=$(cpu_usec)
  m=$(stop_rss)
  say "$(printf '  %d concurrent walks of %s: %.2f s for all, slowest walk %.2f s, CPU %.2f s, %s' "$n" "$name" "$(echo "$t1 - $t0" | bc)" \
    "$(cat "$WORK"/concurrent-* | awk '{ if ($1 > s) s = $1 } END { print s }')" "$(echo "($c1 - $c0) / 1000000" | bc -l)" "$m")"
  check "$name: every concurrent walk reads every entry once" 0 "$(cat "$WORK"/concurrent-* | awk '$1 == "FAIL" || $2 != $3 { n++ } END { print n + 0 }')"
}

FAILED=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else say "FAIL $1: expected [$2], got [$3]"; FAILED=1; fi; }

echo "--- media: $FILMS films, hard links of one 1 s video"
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "$NAME-config" "$NAME-cache" "$NAME-fed" "$NAME-media" >/dev/null 2>&1 || true
docker run --rm -v "$NAME-media:/media" --entrypoint bash "$IMAGE" -c '
  /usr/lib/jellyfin-ffmpeg/ffmpeg -loglevel error -f lavfi -i color=c=black:s=64x36:d=1 -f lavfi -i anullsrc=r=8000:cl=mono \
    -t 1 -c:v libx264 -c:a aac -shortest /media/seed.mkv || exit 1
  mkdir -p /media/film && cd /media/film
  for i in $(seq -w 1 '"$FILMS"'); do
    t="Film $i ($((1950 + 10#$i % 70)))"; mkdir "$t" && ln /media/seed.mkv "$t/$t.mkv"
  done
  ls /media/film | wc -l' || exit 1

echo "--- provider $NAME on :$PORT"
docker run -d --name "$NAME" -p "127.0.0.1:$PORT:8096" -e TZ=UTC \
  -v "$NAME-config:/config" -v "$NAME-cache:/cache" -v "$NAME-fed:/federata" -v "$NAME-media:/media:ro" "$IMAGE" >/dev/null
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
[ "$(get /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Status')" = Active ] || { echo "plugin not active" >&2; exit 1; }
# A provider that is also a consumer has a federated folder, so the scope of every page is worked out as in real use.
cfg=$(get "$PLUGIN" | jq '.FederatedFolderPath = "/federata" | .SyncIntervalSeconds = 86400')
post "$PLUGIN" "$cfg" >/dev/null

echo "--- library and scan"
# No image fetcher, no metadata fetcher: only the probe of each file, which the catalog DTO carries.
opts='{"LibraryOptions":{"EnableInternetProviders":false,"EnableChapterImageExtraction":false,"ExtractChapterImagesDuringLibraryScan":false,
  "EnableTrickplayImageExtraction":false,"ExtractTrickplayImagesDuringLibraryScan":false,
  "TypeOptions":[{"Type":"Movie","MetadataFetchers":[],"MetadataFetcherOrder":[],"ImageFetchers":[],"ImageFetcherOrder":[]}]}}'
post "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=false&paths=/media/film" "$opts" >/dev/null
scan_start=$(date +%s)
post /Library/Refresh >/dev/null
sleep 10
settle
scan_s=$(( $(date +%s) - scan_start ))
get '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaStreams&SortBy=SortName' >"$WORK/films.json"
check "every film is indexed" "$FILMS" "$(jq '.Items | length' "$WORK/films.json")"
check "the films are probed" "$FILMS" "$(jq '[.Items[] | select((.MediaStreams // []) | length > 0)] | length' "$WORK/films.json")"
echo "scan: $scan_s s"

echo "--- the peer"
uid=$(post /Users/New '{"Name":"peer","Password":"jellymesh-test"}' | jq -r .Id)
lib=$(get /JellyMesh/Admin/Peers/Options | jq -r '.Libraries[] | select(.Name == "Film") | .Id')
req PUT "/JellyMesh/Admin/Peers/$uid" "$(jq -n --arg l "$lib" '{SharedLibraryIds: [$l], MaxStreams: 2}')" >/dev/null
peer_login
r=$(peer /JellyMesh/Provider/Libraries)
check "the peer lists its library" 200 "${r%% *}"
peer_lib=$(jq -r '.Items[] | select(.Name == "Film") | .Id' "$WORK/peer-body")
COLS="/JellyMesh/Provider/Collections?parentId=$peer_lib"
ITEMS="/JellyMesh/Provider/Items?parentId=$peer_lib&includeItemTypes=Movie"

# The collections: sizes 2 + floor(18 u^2), so most are small, each of films nobody else has, from a fixed shuffle.
jq -r '.Items[].Id' "$WORK/films.json" >"$WORK/film-ids"
python3 - "$WORK/film-ids" "$COLLECTIONS" >"$WORK/plan" <<'EOF' || exit 1
import random, sys
ids = open(sys.argv[1]).read().split()
rng = random.Random(232)
rng.shuffle(ids)
at = 0
for n in range(int(sys.argv[2])):
    size = 2 + int(18 * rng.random() ** 2)
    if at + size > len(ids):
        sys.exit("not enough films for the collections")
    print(f"Saga {n + 1:04d} " + ",".join(ids[at:at + size]))
    at += size
EOF
sizes=$(awk '{ print gsub(/,/, ",") + 1 }' "$WORK/plan")

{
  echo "# Row 232: cost of GET JellyMesh/Provider/Collections, measured on $(date -u +%F) at $(git -C "$WT" rev-parse --short HEAD)"
  echo
  echo "Provider: Jellyfin 12.4 ($IMAGE) with the plugin, alone on a Docker VM of $(docker info --format '{{.NCPU}} CPUs and {{.MemTotal}}' | awk '{ printf "%s %s %s %.1f GiB", $1, $2, $3, $4 / 1073741824 }')."
  echo "Library: $FILMS films, every one a probed file (hard links of one 1 s video), no metadata or images; scan took $scan_s s."
  echo "Collections: $COLLECTIONS, each of 2-20 films nobody else has, sizes 2 + floor(18 u^2):"
  echo "$sizes" | awk '{ n++; s += $1; b[($1 <= 4) ? "2-4" : ($1 <= 9) ? "5-9" : ($1 <= 14) ? "10-14" : "15-20"]++ }
    END { printf "  %d member films in all (mean %.1f); by size 2-4: %d, 5-9: %d, 10-14: %d, 15-20: %d\n", s, s / n, b["2-4"], b["5-9"], b["10-14"], b["15-20"] }'
  echo "Requests: as the dedicated user granted the film library, from the host over loopback; times are curl time_total."
  echo "The first call of a page is right after the previous step, the repeats follow it at once. CPU is the usage_usec"
  echo "of the container's cgroup over the walk; rss is the resident set of the Jellyfin process sampled every 0.25 s."
  echo "The consumer reads this route in pages of $PAGE, one after the other, with a 30 s timeout per request."
} >"$OUT"

made=0
expected_members=0
for checkpoint in $CHECKPOINTS; do
  echo "--- collections up to $checkpoint"
  # One at a time: concurrent creations race on the file of the collections folder.
  t0=$(date +%s)
  sed -n "$((made + 1)),${checkpoint}p" "$WORK/plan" | while read -r saga num ids; do
    curl -sS -o /dev/null -w '%{http_code}\n' -X POST -H "$ACCEPT" -H "Authorization: $AUTH, Token=\"$(cat "$WORK/token")\"" \
      "$U/Collections?name=$saga%20$num&ids=$ids"
  done >"$WORK/created"
  echo "created $(grep -c '^200$' "$WORK/created") of $(wc -l <"$WORK/created" | tr -d ' ') in $(( $(date +%s) - t0 )) s"
  check "every collection is created" 0 "$(grep -vc '^200$' "$WORK/created")"
  made=$checkpoint
  expected_members=$(echo "$sizes" | head -n "$made" | awk '{ s += $1 } END { print s }')
  sleep 5
  settle
  check "collections on the server" "$made" "$(get '/Items?Recursive=true&IncludeItemTypes=BoxSet&Limit=0' | jq .TotalRecordCount)"

  # A fresh process, so that the first call of each route is the first since the start.
  docker restart "$NAME" >/dev/null
  wait_ready
  sleep 5
  login
  peer_login
  JPID=$(jpid)
  settle
  docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' >"$WORK/load-$checkpoint.txt" 2>/dev/null
  say
  say "== $made collections, $expected_members member films (other containers: $(grep -v "^$NAME " "$WORK/load-$checkpoint.txt" | awk '{ printf "%s %s; ", $1, $2 }'))"
  say " idle after restart: $(rss_mib) MiB resident"
  measure Collections "$COLS" "$expected_members"
  concurrent Collections "$COLS" 4
  measure Items "$ITEMS" "$FILMS"
  concurrent Items "$ITEMS" 4
  say " resident after the measurements: $(rss_mib) MiB, peak of the process $(in_box "awk '/VmHWM/ { print int(\$2 / 1024) }' /proc/$JPID/status") MiB"
done

# Every film of the planned collections comes up once, with the name of its collection.
start=0; total=1
: >"$WORK/entries"
while [ "$start" -lt "$total" ]; do
  peer "$COLS&startIndex=$start&limit=$PAGE" >/dev/null
  total=$(jq .TotalRecordCount "$WORK/peer-body")
  jq -r '.Items[] | "\(.Id) \(.Name)"' "$WORK/peer-body" >>"$WORK/entries"
  n=$(jq '.Items | length' "$WORK/peer-body"); [ "$n" -gt 0 ] || break
  start=$((start + n))
done
check "every entry names the planned collection" ok "$(python3 - "$WORK/plan" "$WORK/entries" <<'EOF'
import sys
want = {}
for line in open(sys.argv[1]):
    saga, num, ids = line.split()
    for i in ids.split(","):
        want[i.replace("-", "")] = f"{saga} {num}"
got = [line.rstrip("\n").split(" ", 1) for line in open(sys.argv[2]) if line.strip()]
ids = [g[0] for g in got]
bad = [g for g in got if want.get(g[0]) != g[1]]
print("ok" if not bad and len(ids) == len(set(ids)) == len(want) else f"{len(got)} entries, {len(set(ids))} distinct, {len(want)} planned, {len(bad)} wrong, e.g. {bad[:2]}")
EOF
)"
say
say "failed checks: $FAILED"
echo "results in $OUT"
exit $FAILED
