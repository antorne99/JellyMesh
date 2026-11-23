#!/usr/bin/env bash
# Do the linked versions of a provider show its stubs to a peer? A film or an episode of the provider that also has a stub
# of another server is linked to it as a version (rows 63/64). No stub id, stub path or stub content may reach the peer:
# not in the provider API, not in a stock route, not in the index of the consumer.
# Runs on the stopped shared stack and creates no container:
#   p  jellymesh-test-remoto-jellymesh-1  the provider, with the plugin, which federates s as a stock server
#   s  jellymesh-test-remoto-standard-1   the stock server, then the consumer of p, with the plugin
# Two layouts on p: the stubs in the shared libraries, then in libraries of their own that are not shared. Each one with a
# peer that may enter every library, then with one limited to the shared libraries. A leak does not stop the run: the table
# at the end has the leaks of each request in each layout.
# Runs on 0.5 and on 0.6. Puts /config of both servers back as it was and stops them at the end. KEEP=1 leaves them running
# as they are, to look at a failure, and a run with DOWN=1 and the same WORK puts them back.
# usage: DLL=<folder of a Release build, with its build.yaml> WORK=<private scratch folder> linked-versions.sh
# Exits with 0 when nothing leaked and every control passed, 3 when the run is complete but a stub reached the peer.
set -euo pipefail
umask 077
DLL=${DLL:?point DLL at the folder of a Release build}
WORK=${WORK:?point WORK at a scratch folder}
P=${P:-jellymesh-test-remoto-jellymesh-1}
S=${S:-jellymesh-test-remoto-standard-1}
P_URL_INSIDE=${P_URL_INSIDE:-http://remoto-jellymesh:8096}
S_URL_INSIDE=${S_URL_INSIDE:-http://remoto-standard:8096}
MAX_USED_MB=${MAX_USED_MB:-7168}
PEER=jm-linked-peer
FED_P=/federata
FED_S=/tmp/jm-linked-fed
DATA=/config/plugins/Jellyfin.Plugin.JellyMesh
XML=/config/plugins/configurations/Jellyfin.Plugin.JellyMesh.xml
VERSION=$(sed -n 's/^version: "\(.*\)"$/\1/p' "$DLL/build.yaml")
GUID=28c43ea1-fb5a-4b3a-8e6e-30aa3245c653
ACCEPT='Accept: application/json; profile="PascalCase"'
FAILED=0
LEAKS=0
DONE=0
mkdir -p "$WORK"

box() { case $1 in p) echo "$P" ;; s) echo "$S" ;; esac; }
port() { docker port "$(box "$1")" 8096/tcp | head -1 | sed 's/.*://'; }
base() { cat "$WORK/base-$1"; }
check() { if [ "$2" = "$3" ]; then echo "PASS $1: $3"; else echo "FAIL $1: expected $2, got $3"; FAILED=1; fi; }
# row <request> <film|episode|all> <leak|no|unknown>: one line of the table at the end.
row() {
  echo "$LAYOUT|$1|$2|$3" >>"$WORK/results"
  [ "$3" != leak ] || LEAKS=$((LEAKS + 1))
}

# req <p|s> <method> <path> [body]: prints the body; the status goes to $WORK/status. Tokens stay in curl config files.
req() {
  curl -sS -K "$WORK/auth-$1" -o "$WORK/body" -w '%{http_code}' -X "$2" -H "$ACCEPT" -H 'Content-Type: application/json' \
    ${4:+-d "$4"} "$(base "$1")$3" >"$WORK/status"
  cat "$WORK/body"
}
get() { req "$1" GET "$2"; }
post() { req "$1" POST "$2" "${3:-}"; }
expect() { # expect <status> <what>
  [ "$(cat "$WORK/status")" = "$1" ] || { echo "$2 failed: $(cat "$WORK/status") $(head -c 300 "$WORK/body")" >&2; return 1; }
}
auth_file() { # auth_file <file> <device> [token]
  printf 'header = "Authorization: MediaBrowser Client=\\"JellyMeshLinked\\", Device=\\"linked\\", DeviceId=\\"%s\\", Version=\\"0.1\\"%s"\n' \
    "$2" "${3:+, Token=\\\"$3\\\"}" >"$1"
}
login() { # login <p|s>
  auth_file "$WORK/auth-$1" jm-linked-admin
  local token
  token=$(post "$1" /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken)
  auth_file "$WORK/auth-$1" jm-linked-admin "$token"
}
peer_login() {
  auth_file "$WORK/auth-peer" jm-linked-peer
  local token
  token=$(curl -sS -K "$WORK/auth-peer" -X POST -H "$ACCEPT" -H 'Content-Type: application/json' \
    -d "{\"Username\":\"$PEER\",\"Pw\":\"jellymesh-test\"}" "$(base p)/Users/AuthenticateByName" | jq -r .AccessToken)
  auth_file "$WORK/auth-peer" jm-linked-peer "$token"
}
memory() {
  local used
  used=$(docker exec "$S" awk '/MemTotal/{t=$2} /MemAvailable/{a=$2} END{print int((t-a)/1024)}' /proc/meminfo)
  [ "$used" -le "$MAX_USED_MB" ] || { echo "the VM uses $used MB, stopping" >&2; return 1; }
}
wait_ready() {
  local i
  echo "http://127.0.0.1:$(port "$1")" >"$WORK/base-$1"
  for i in $(seq 1 120); do
    if curl -fsS "$(base "$1")/System/Info/Public" 2>/dev/null | jq -e .Version >/dev/null 2>&1 &&
      [ "$(curl -s -o /dev/null -w '%{http_code}' "$(base "$1")/health")" = 200 ]; then return 0; fi
    sleep 1
  done
  echo "$1 is not ready" >&2
  return 1
}
# scratch <p|s>: deletes what the run left in the temporary folders of a server.
scratch() {
  docker exec "$(box "$1")" sh -c '[ ! -f /tmp/jm-linked-start ] || find /tmp/jellyfin /cache/transcodes -type f ! -name .jellyfin-transcode -newer /tmp/jm-linked-start -delete' 2>/dev/null || true
}
up() { docker start "$(box "$1")" >/dev/null; wait_ready "$1"; login "$1"; }
restart() { docker restart "$(box "$1")" >/dev/null; sleep 2; wait_ready "$1"; login "$1"; }
task_end() { get "$1" /ScheduledTasks | jq -r --arg k "$2" '.[] | select(.Key == $k) | .LastExecutionResult.EndTimeUtc // ""'; }
run_task() { # run_task <p|s> <key>: runs a scheduled task and waits for it to end
  local id last i
  for i in $(seq 1 300); do
    [ "$(get "$1" /ScheduledTasks | jq -r --arg k "$2" '.[] | select(.Key == $k) | .State')" = Idle ] && break
    sleep 1
  done
  last=$(task_end "$1" "$2")
  id=$(get "$1" /ScheduledTasks | jq -r --arg k "$2" '.[] | select(.Key == $k) | .Id')
  post "$1" "/ScheduledTasks/Running/$id" >/dev/null
  for i in $(seq 1 300); do
    [ "$(task_end "$1" "$2")" != "$last" ] && { sleep 2; return 0; }
    sleep 1
  done
  echo "task $2 of $1 did not end" >&2
  return 1
}
scan() { run_task "$1" RefreshLibrary; }
sync_now() {
  local i
  post "$1" /JellyMesh/Admin/Sync >/dev/null
  for i in $(seq 1 240); do
    [ "$(get "$1" /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return 0
    sleep 1
  done
  echo "sync of $1 did not end" >&2
  return 1
}
libraries() { get "$1" /Library/VirtualFolders | jq -c '[.[] | [.Name, (.Locations | sort)]] | sort'; }
install() { # install <p|s>: the plugin, on a server that has none
  local c
  c=$(box "$1")
  if docker exec "$c" sh -c 'ls -d /config/plugins/JellyMesh_* /config/plugins/Jellyfin.Plugin.JellyMesh' >/dev/null 2>&1; then
    echo "$c already has the plugin or its data, not touching it" >&2
    return 1
  fi
  touch "$WORK/installed-$1"
  jq -n --arg v "$VERSION" '{guid: "28c43ea1-fb5a-4b3a-8e6e-30aa3245c653", name: "JellyMesh", version: $v, targetAbi: "12.1.0.0",
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$WORK/meta.json"
  docker exec "$c" mkdir -p "/config/plugins/JellyMesh_$VERSION"
  docker cp "$DLL/Jellyfin.Plugin.JellyMesh.dll" "$c:/config/plugins/JellyMesh_$VERSION/" >/dev/null 2>&1
  docker cp "$WORK/meta.json" "$c:/config/plugins/JellyMesh_$VERSION/" >/dev/null 2>&1
  restart "$1"
  check "$1 runs the plugin" Active "$(get "$1" /Plugins | jq -r '.[] | select(.Name == "JellyMesh") | .Status')"
  get "$1" /JellyMesh/Admin/Settings >/dev/null
  if [ "$(cat "$WORK/status")" = 200 ]; then echo new >"$WORK/api-$1"; else echo old >"$WORK/api-$1"; fi
}
api() { cat "$WORK/api-$1"; }
settings() { # settings <p|s> <folder>: 0.6 has its own settings route, 0.5 the stock plugin configuration
  local path=/JellyMesh/Admin/Settings method=PUT c
  [ "$(api "$1")" = new ] || { path=/Plugins/$GUID/Configuration; method=POST; }
  c=$(get "$1" "$path" | jq -c --arg f "$2" '.FederatedFolderPath = $f | .SyncIntervalSeconds = 86400 | del(.FixedByEnvironment)')
  docker exec "$(box "$1")" mkdir -p "$2"
  req "$1" "$method" "$path" "$c" >/dev/null
  case $(cat "$WORK/status") in 20*) ;; *) expect 204 "saving the settings of $1" ;; esac
  check "$1 federated folder" "$2" "$(get "$1" "$path" | jq -r .FederatedFolderPath)"
}
add_remote() { # add_remote <p|s> <name> <url> <user> -> server id, with no library made by the plugin
  local id form ids
  id=$(post "$1" /JellyMesh/Admin/Servers "$(jq -n --arg n "$2" --arg u "$3" --arg x "$4" \
    '{Name: $n, Url: $u, Username: $x, Password: "jellymesh-test", LibraryMode: "None"}')" | jq -r '.Id // empty')
  [ -n "$id" ] || { echo "adding $2 to $1 failed: $(cat "$WORK/status") $(head -c 300 "$WORK/body")" >&2; return 1; }
  if [ "$(api "$1")" = new ]; then
    form=$(get "$1" /JellyMesh/Admin/Status | jq -c --arg id "$id" '.Servers[] | select(.Id == $id) | {Name, Url, Username: "", Password: ""}')
    ids=$(post "$1" "/JellyMesh/Admin/Servers/$id/Edit/Check" "$form" | jq -c '[.Libraries[].Id]')
    req "$1" PUT "/JellyMesh/Admin/Servers/$id" "$(jq -c --argjson l "$ids" '. + {LibraryIds: $l}' <<<"$form")" >/dev/null
  else
    ids=$(get "$1" "/JellyMesh/Admin/Servers/$id/Libraries" | jq -c '{LibraryIds: map(.Id)}')
    post "$1" "/JellyMesh/Admin/Servers/$id/Libraries" "$ids" >/dev/null
  fi
  case $(cat "$WORK/status") in 20*) ;; *) expect 200 "importing from $2" ;; esac
  echo "$id"
}
dashed() { local id; id=$(tr -d - <<<"$1"); echo "${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"; }
copy_db() { # copy_db <p|s> <path> -> a copy with its write-ahead log
  local to
  to=$WORK/$1-$(basename "$2")
  rm -f "$to" "$to-wal" "$to-shm"
  docker cp "$(box "$1"):$2" "$to" >/dev/null 2>&1
  docker cp "$(box "$1"):$2-wal" "$to-wal" >/dev/null 2>&1 || true
  echo "$to"
}
sql() { sqlite3 -separator '|' "$(copy_db "$1" /config/data/jellyfin.db)" "$2"; }
user_id() { get p /Users | jq -r --arg n "$PEER" '.[] | select(.Name == $n) | .Id'; }
library_id() { get p /JellyMesh/Admin/Peers/Options | jq -r --arg n "$1" '.Libraries[] | select(.Name == $n) | .Id'; }

# peer <label> <method> <path> [body] [extra curl option...]: a request of the peer, kept in $WORK/seen. Sets CODE and SEEN.
N=0
peer() {
  local label=$1 method=$2 path=$3 body=${4:-}
  shift 3; [ $# -gt 0 ] && shift
  N=$((N + 1))
  SEEN=$WORK/seen/$LAYOUT-$(printf %03d $N)-$label
  CODE=$(curl -sS -K "$WORK/auth-peer" --max-time 30 -o "$SEEN" -w '%{http_code}' -X "$method" -H "$ACCEPT" -H 'Content-Type: application/json' \
    ${body:+-d "$body"} "$@" "$(base p)$path") || true
  echo "$LAYOUT $label $method $(sed -E 's/(ApiKey|api_key)=[^&"]*/\1=x/Ig' <<<"$path") -> $CODE $(wc -c <"$SEEN" 2>/dev/null | tr -d ' ') bytes" >>"$WORK/requests.txt"
  touch "$SEEN"
  [ "$(wc -c <"$SEEN" | tr -d ' ')" -le 1048576 ] || { head -c 1048576 "$SEEN" >"$SEEN.cut"; mv "$SEEN.cut" "$SEEN"; }
}

# What an answer to the peer says. served: any success is a leak. held: a success with a stub id or path in it.
served() { case $CODE in 2*) echo leak ;; 429 | 000) echo unknown ;; *) echo no ;; esac; }
held() { if [ "$(served)" = leak ]; then [ -n "$(holds "$SEEN")" ] && echo leak || echo no; else served; fi; }
# offered <real>: a success that offers any version but the real one.
offered() {
  if [ "$(served)" != leak ]; then served; return 0; fi
  if [ -n "$(holds "$SEEN")" ] || [ "$(jq --arg r "$1" '[(.MediaSources // [])[].Id | ascii_downcase | gsub("-"; "")] - [$r] | length' "$SEEN" 2>/dev/null || echo 1)" != 0 ]; then echo leak; else echo no; fi
}
# streamed: a success whose bytes are not those of a file of p, no verdict when both servers have the same file.
streamed() {
  local hash
  if [ "$(served)" != leak ]; then served; return 0; fi
  hash=$(head -c 65536 "$SEEN" | shasum -a 256 | cut -d' ' -f1)
  if ! grep -qFx "$hash" "$WORK/heads-p"; then echo leak; elif grep -qFx "$hash" "$WORK/heads-s"; then echo unknown; else echo no; fi
}
# read_stubs: the ids of every stub of p, and the pairs "stub|real|real path" of those linked to a real film or episode.
read_stubs() {
  sql p "select lower(replace(Id, '-', '')) from BaseItems where Path like '$FED_P/%.strm';" | sort -u >"$WORK/stubs"
  sql p "select lower(replace(s.Id, '-', '')), lower(replace(r.Id, '-', '')), r.Path from BaseItems s join BaseItems r on r.Id = s.PrimaryVersionId
    where s.Path like '$FED_P/%.strm' and r.Path not like '$FED_P/%' order by r.Path;" >"$WORK/pairs"
  while read -r id; do dashed "$id"; done <"$WORK/stubs" | cat "$WORK/stubs" - >"$WORK/needles"
}
link_up() { # scans and syncs p until some stubs are linked to a real film
  local i
  for i in 1 2 3 4; do
    scan p
    sync_now p
    read_stubs
    [ "$(grep -c '/film/' "$WORK/pairs" || true)" -ge 4 ] && return 0
  done
  return 1
}
# holds <file>: the stub ids a kept answer holds, or a mark of a stub path.
holds() { { grep -aoiFf "$WORK/needles" "$1" || true; grep -aoE "$FED_P/|\.strm" "$1" || true; } 2>/dev/null | sort -u | head -5 | paste -sd' ' -; }
# heads <p|s>: the hash of the first 64 KiB of each video of a server, to tell whose file was served.
heads() {
  docker exec "$(box "$1")" sh -c "find /media -type f -size +100k \\( -name '*.mkv' -o -name '*.mp4' \\) -exec sh -c 'head -c 65536 \"\$1\" | sha256sum | cut -d\" \" -f1' _ {} \;" | sort -u
}

control() { # an admin sees both versions of each pair
  local real stub path kind ok=0 all=0
  while IFS='|' read -r stub real path; do
    all=$((all + 1))
    if get p "/Items?Ids=$real&Fields=MediaSources" | jq -e --arg s "$stub" --arg r "$real" \
      '[.Items[0].MediaSources[].Id | ascii_downcase | gsub("-"; "")] | (index($s) != null and index($r) != null)' >/dev/null; then
      ok=$((ok + 1))
    else
      echo "  no second version for the admin: $path"
    fi
  done <"$WORK/pairs"
  echo "  $(wc -l <"$WORK/stubs" | tr -d ' ') stubs, $all linked to a real item"
  [ "$all" -gt 0 ] || { echo "no linked pair, the check would prove nothing" >&2; return 1; }
  check "$LAYOUT positive control: an admin sees both versions linked" "$all" "$ok"
  [ "$ok" = "$all" ] || return 1
  for kind in film serie; do
    IFS='|' read -r stub real path < <(grep "/$kind/" "$WORK/pairs")
    check "$LAYOUT positive control: an admin streams the stub version of a $kind" 206 "$(curl -sS -K "$WORK/auth-p" --max-time 60 -r 0-65535 -o "$WORK/body" -w '%{http_code}' \
      "$(base p)/Videos/$real/stream?static=true&mediaSourceId=$stub" || true)"
    check "$LAYOUT and its bytes are a file of the third server" 1 "$(head -c 65536 "$WORK/body" | shasum -a 256 | cut -d' ' -f1 | grep -cFxf "$WORK/heads-x" || true)"
  done
}

peer_checks() {
  local lib type kind real stub path name f films=0 episodes=0
  rm -rf "$WORK/seen"; mkdir -p "$WORK/seen"; N=0; : >"$WORK/requests.txt"
  scratch p
  peer_login
  peer libraries GET /JellyMesh/Provider/Libraries
  check "$LAYOUT peer sees the shared libraries" "Film,Serie" "$(jq -r '[.Items[].Name] | sort | join(",")' "$SEEN")"
  for lib in "Film Movie" "Serie Series" "Serie Season" "Serie Episode"; do
    type=${lib#* }
    peer "list-$type" GET "/JellyMesh/Provider/Items?parentId=$(library_id "${lib% *}")&includeItemTypes=$type&limit=500"
    check "$LAYOUT provider list of $type" 200 "$CODE"
    echo "  $type: $(jq -r '.TotalRecordCount' "$SEEN") items, most versions of one $(jq -r '[.Items[] | (.MediaSources // []) | length] | max // 0' "$SEEN")"
    cp "$SEEN" "$WORK/list-$type"
    case $type in Series | Season) row "Provider/Items, series and seasons" all "$(held)" ;; esac
  done
  peer collections GET "/JellyMesh/Provider/Collections?parentId=$(library_id Film)&limit=500"
  row "Provider/Collections" all "$(held)"
  peer changes GET "/JellyMesh/Provider/Changes?cursor=$(cat "$WORK/cursor")"
  row "Provider/Changes" all "$(held)"
  peer stock-items GET "/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=MediaSources,Path"
  row "/Items" all "$(held)"
  while IFS='|' read -r stub real path; do
    name=$(basename "$path")
    if grep -q '/film/' <<<"$path"; then type=Movie kind=film; films=$((films + 1)); else type=Episode kind=episode; episodes=$((episodes + 1)); fi
    if grep -qi "$stub" "$WORK/list-$type"; then row "Provider/Items of the library" $kind leak; else row "Provider/Items of the library" $kind no; fi
    peer provider-ids GET "/JellyMesh/Provider/Items?ids=$real&includeItemTypes=$type"
    check "$LAYOUT provider item of $name has the real version" true "$(jq --arg r "$real" '[.Items[].MediaSources[].Id | ascii_downcase | gsub("-"; "")] | index($r) != null' "$SEEN" 2>/dev/null || echo unreadable)"
    row "Provider/Items?ids={real}" $kind "$(held)"
    peer provider-stub GET "/JellyMesh/Provider/Items?ids=$stub&includeItemTypes=$type"
    if [ "$(jq '.Items | length' "$SEEN" 2>/dev/null || echo 0)" = 0 ]; then row "Provider/Items?ids={stub}" $kind no; else row "Provider/Items?ids={stub}" $kind leak; fi
    peer item-real GET "/Items/$real"
    row "/Items/{real}" $kind "$(held)"
    # The positive twin: the real version is served.
    peer info GET "/Items/$real/PlaybackInfo"
    check "$LAYOUT PlaybackInfo of $name" 200 "$CODE"
    row "PlaybackInfo, no version asked" $kind "$(offered "$real")"
    peer info-real POST "/Items/$real/PlaybackInfo?mediaSourceId=$real" '{}'
    check "$LAYOUT PlaybackInfo of $name, real version" 200 "$CODE"
    row "PlaybackInfo, real version" $kind "$(offered "$real")"
    peer stream-real GET "/Videos/$real/stream?static=true&mediaSourceId=$real" '' -r 0-65535
    check "$LAYOUT stream of $name, real version" 206 "$CODE"
    check "$LAYOUT the real version of $name is a file of p" 1 "$(head -c 65536 "$SEEN" | shasum -a 256 | cut -d' ' -f1 | grep -cFxf "$WORK/heads-p" || true)"
    # The stub version, in each route a peer may call.
    peer info-stub POST "/Items/$real/PlaybackInfo?mediaSourceId=$stub" '{}'
    row "PlaybackInfo, stub version" $kind "$(offered "$real")"
    peer info-stub-body POST "/Items/$real/PlaybackInfo" "{\"MediaSourceId\":\"$stub\",\"AutoOpenLiveStream\":true}"
    row "PlaybackInfo, stub version in the body" $kind "$(offered "$real")"
    peer info-stub-dashed POST "/Items/$real/PlaybackInfo?mediaSourceId=$(dashed "$stub")" '{}'
    row "PlaybackInfo, stub version with dashes" $kind "$(offered "$real")"
    peer static-stub GET "/Videos/$real/stream?static=true&mediaSourceId=$stub" '' -r 0-65535
    row "stream static, stub version" $kind "$(streamed)"
    f=$(streamed)
    [ "$f" != leak ] || { head -c 65536 "$SEEN" | shasum -a 256 | cut -d' ' -f1 | grep -qFxf "$WORK/heads-s" || f=no; }
    row "  of which the bytes are a file of the third server" $kind "$f"
    peer static-stub-mkv GET "/Videos/$real/stream.mkv?static=true&mediaSourceId=$stub" '' -r 0-65535
    row "stream.mkv static, stub version" $kind "$(streamed)"
    peer static-stub-dashed GET "/Videos/$real/stream?static=true&mediaSourceId=$(dashed "$stub")" '' -r 0-65535
    row "stream static, stub version with dashes" $kind "$(streamed)"
    peer subtitles-stub GET "/Videos/$real/$stub/Subtitles/0/Stream.srt"
    row "subtitles, stub version" $kind "$(served)"
    if [ "$kind$films" = film1 ] || [ "$kind$episodes" = episode1 ]; then
      peer remux-stub GET "/Videos/$real/stream?mediaSourceId=$stub&videoCodec=copy&audioCodec=copy&container=ts" '' -r 0-65535 --max-time 15
      row "stream remuxed, stub version" $kind "$(served)"
      hls_checks "$real" "$stub" $kind
    fi
    peer info-of-stub POST "/Items/$stub/PlaybackInfo" '{}'
    row "PlaybackInfo of the stub item" $kind "$(offered "$real")"
    peer stream-of-stub GET "/Videos/$stub/stream?static=true" '' -r 0-65535
    row "stream of the stub item" $kind "$(streamed)"
    peer image-of-stub GET "/Items/$stub/Images/Primary"
    row "image of the stub item" $kind "$(served)"
    peer segments-of-stub GET "/MediaSegments/$stub"
    row "media segments of the stub item" $kind "$(served)"
  done <"$WORK/pairs"
  cp "$WORK/requests.txt" "$WORK/requests-$LAYOUT.txt"
  rm -rf "$WORK/seen"
}
# hls_checks <real> <stub> <film|episode>: the HLS session PlaybackInfo offers for a version, down to a segment. The real
# version first, as the positive twin.
hls_checks() {
  local real=$1 stub=$2 kind=$3 id url play profile code
  profile='{"MediaSourceId":"VERSION","DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[],"TranscodingProfiles":[{"Type":"Video","Protocol":"hls","Container":"ts","VideoCodec":"h264","AudioCodec":"aac","Context":"Streaming"}]}}'
  for id in "$real" "$stub"; do
    peer hls-info POST "/Items/$real/PlaybackInfo?mediaSourceId=$id" "${profile/VERSION/$id}"
    url=$(jq -r '.MediaSources[0].TranscodingUrl // empty' "$SEEN" 2>/dev/null | sed -E 's/[?&](ApiKey|api_key)=[^&]*//Ig' || true)
    play=$(jq -r '.PlaySessionId // empty' "$SEEN" 2>/dev/null || true)
    if [ -n "$url" ]; then
      grep -q '?' <<<"$url" || url=${url/&/?}
      peer hls-master GET "$url" '' --max-time 30
      code=$CODE
      if [ "$code" = 200 ]; then
        f=$(grep -v '^#' "$SEEN" | head -1 | tr -d '\r' | sed -E 's/&(ApiKey|api_key)=[^&]*//Ig' || true)
        peer hls-variant GET "${url%%master.m3u8*}$f" '' --max-time 30
        f=$(grep -v '^#' "$SEEN" | head -1 | tr -d '\r' | sed -E 's/&(ApiKey|api_key)=[^&]*//Ig' || true)
        [ "$CODE" != 200 ] || [ -z "$f" ] || peer hls-segment GET "${url%%master.m3u8*}$f" '' --max-time 60
      fi
    fi
    if [ "$id" = "$real" ]; then
      check "$LAYOUT HLS of a $kind, real version" "200 hls-segment" "$CODE $(sed -E 's/.*-(hls-.*)/\1/' <<<"$(basename "$SEEN")")"
    else
      row "HLS, stub version (to the last file)" $kind "$(served)"
      echo "  HLS of the stub version of a $kind ended at $(sed -E 's/.*-(hls-.*)/\1/' <<<"$(basename "$SEEN")") $CODE"
    fi
    [ -z "$play" ] || peer hls-stop DELETE "/Videos/ActiveEncodings?deviceId=jm-linked-peer&playSessionId=$play"
  done
}

# index_of_s: the index of the consumer, one entry a line. 0.6 has it in its database, 0.5 in a file.
index_of_s() {
  if [ "$(api s)" = new ]; then
    sqlite3 "$(copy_db s "$DATA/jellymesh.db")" 'select data from index_items'
  else
    docker exec "$S" cat "$DATA/federation-index.json" 2>/dev/null | jq -c '.. | objects | select((.ItemId // .itemId) != null and (.Versions // .versions) != null)' || true
  fi
}
consumer_checks() { # a first import of p on s, so that nothing comes from an earlier pass
  local id stub real path kind written
  plugin_on
  case $LAYOUT in
    *-limited)
      # The consumer of the variant before, which had imported p while the peer had every library.
      run_task s JellyMeshReconciliation
      index_of_s | tr -d - | tr 'A-Z' 'a-z' >"$WORK/index.plain"
      if grep -qFf "$WORK/stubs" "$WORK/index.plain"; then row "index of the consumer from before, after a reconciliation" all leak; else row "index of the consumer from before, after a reconciliation" all no; fi
      check "$LAYOUT that consumer still has the linked real items" "$(cut -d'|' -f2 "$WORK/pairs" | sort -u | wc -l | tr -d ' ')" \
        "$(grep -oFf <(cut -d'|' -f2 "$WORK/pairs" | sort -u) "$WORK/index.plain" | sort -u | wc -l | tr -d ' ')"
      ;;
  esac
  [ ! -f "$WORK/consumer-id" ] || { req s DELETE "/JellyMesh/Admin/Servers/$(cat "$WORK/consumer-id")" >/dev/null; rm -f "$WORK/consumer-id"; }
  sync_now s
  check "$LAYOUT consumer index before the import" 0 "$(index_of_s | wc -l | tr -d ' ')"
  id=$(add_remote s Provider "$P_URL_INSIDE" "$PEER")
  echo "$id" >"$WORK/consumer-id"
  sync_now s
  echo "  consumer mode: $(get s /JellyMesh/Admin/Status | jq -r --arg id "$id" '.Servers[] | select(.Id == $id) | .Mode')"
  index_of_s >"$WORK/index.jsonl"
  jq -c '{Kind: (.Kind // .kind), ItemId: (.ItemId // .itemId), Name: (.Name // .name), Versions: [(.Versions // .versions // [])[] | (.MediaSourceId // .mediaSourceId)]}' "$WORK/index.jsonl" >"$WORK/imported-$LAYOUT.jsonl"
  echo "  imported: $(jq -s -r 'group_by(.Kind) | map("\(.[0].Kind) \(length)") | join(", ")' "$WORK/imported-$LAYOUT.jsonl"), versions $(jq -s '[.[].Versions | length] | add // 0' "$WORK/imported-$LAYOUT.jsonl"), most of one item $(jq -s '[.[].Versions | length] | max // 0' "$WORK/imported-$LAYOUT.jsonl")"
  check "$LAYOUT consumer imported the linked real items" "$(cut -d'|' -f2 "$WORK/pairs" | sort -u | wc -l | tr -d ' ')" \
    "$(jq -r '.ItemId | ascii_downcase | gsub("-"; "")' "$WORK/imported-$LAYOUT.jsonl" | sort -u | grep -cFf <(cut -d'|' -f2 "$WORK/pairs" | sort -u) || true)"
  if [ "$(api s)" = new ]; then
    written=$(sqlite3 "$(copy_db s "$DATA/jellymesh.db")" 'select media_source_id from stub_entry_sources union select item_id from stub_entry_sources')
  else
    written=$(docker exec "$S" cat "$DATA/federated-stubs.json" 2>/dev/null || true)
  fi
  tr -d - <<<"$written" | tr 'A-Z' 'a-z' >"$WORK/written"
  tr -d - <"$WORK/index.jsonl" | tr 'A-Z' 'a-z' >"$WORK/index.plain"
  while IFS='|' read -r stub real path; do
    if grep -q '/film/' <<<"$path"; then kind=film; else kind=episode; fi
    if grep -q "$stub" "$WORK/index.plain"; then row "consumer index" $kind leak; else row "consumer index" $kind no; fi
    if grep -q "$stub" "$WORK/written"; then row "consumer stub sources" $kind leak; else row "consumer stub sources" $kind no; fi
  done <"$WORK/pairs"
  if grep -qFf "$WORK/stubs" "$WORK/index.plain"; then row "consumer index, any stub id" all leak; else row "consumer index, any stub id" all no; fi
}
# plugin_on / plugin_off: s is the consumer, or the stock server that p federates.
plugin_on() {
  [ -f "$WORK/installed-s" ] || { install s; settings s "$FED_S"; return 0; }
  ! docker exec "$S" test -d /config/jm-linked-off || { docker exec "$S" mv /config/jm-linked-off "/config/plugins/JellyMesh_$VERSION"; restart s; }
}
plugin_off() {
  ! docker exec "$S" test -d "/config/plugins/JellyMesh_$VERSION" || { docker exec "$S" mv "/config/plugins/JellyMesh_$VERSION" /config/jm-linked-off; restart s; }
}

policy() { # policy <jq filter>
  local uid p
  uid=$(user_id)
  p=$(get p "/Users/$uid" | jq -c ".Policy | $1")
  post p "/Users/$uid/Policy" "$p" >/dev/null
  expect 204 "the policy of the peer"
}
limited() {
  local ids
  ids=$(for n in Film Serie; do library_id "$n"; done | jq -R . | jq -sc .)
  policy ".EnableAllFolders = false | .EnabledFolders = $ids"
}
layout() { # layout <name>: the checks of one layout, for an unlimited peer and a limited one
  LAYOUT=$1-all
  echo "== $LAYOUT"
  policy '.EnableAllFolders = true | .EnabledFolders = []'
  plugin_off; control; memory; peer_checks; consumer_checks
  LAYOUT=$1-limited
  echo "== $LAYOUT"
  limited
  plugin_off; control; memory; peer_checks; consumer_checks
}
# table: for each request, the leaks out of the tries in each layout, films then episodes.
table() {
  python3 -I - "$WORK/results" <<'EOF'
import sys
rows, layouts, cells, total = [], [], {}, {}
for line in open(sys.argv[1]):
    layout, name, kind, value = line.rstrip("\n").split("|")
    if layout not in layouts: layouts.append(layout)
    if name not in rows: rows.append(name)
    cell = cells.setdefault((name, layout), {}).setdefault(kind, [0, 0, 0])
    cell[0] += value == "leak"; cell[1] += 1; cell[2] += value == "unknown"
    total[layout] = total.get(layout, 0) + (value == "leak")
def show(cell):
    parts = []
    for kind in ("film", "episode", "all"):
        if kind in cell:
            leak, total, unknown = cell[kind]
            parts.append(f"{kind[0]} {leak}/{total}" + (f" ?{unknown}" if unknown else ""))
    return ", ".join(parts)
print("leaks/tries, f film, e episode, a whole answer, ? no verdict")
print("| request | " + " | ".join(layouts) + " |")
for name in rows:
    print(f"| {name} | " + " | ".join(show(cells.get((name, l), {})) for l in layouts) + " |")
print("| leaks in all | " + " | ".join(str(total[l]) for l in layouts) + " |")
EOF
}
# files <before> <after> <extra|check>: compares two copies of /config, logs apart: the new files, or every difference.
files() {
  python3 -I - "$@" <<'EOF'
import hashlib, sys, tarfile
def read(path):
    out = {}
    with tarfile.open(path) as tar:
        for m in tar:
            if m.name.startswith("config/log"): continue
            body = hashlib.sha256(tar.extractfile(m).read()).hexdigest() if m.isfile() else m.linkname
            out[m.name] = (m.mode, m.uid, m.gid, m.type, body)
    return out
before, after = read(sys.argv[1]), read(sys.argv[2])
if sys.argv[3] == "extra":
    for name in sorted(set(after) - set(before), reverse=True):
        if not name.endswith(("-wal", "-shm")): print(name)
else:
    for name in sorted(set(before) | set(after)):
        if before.get(name) != after.get(name): print(name)
EOF
}

# down: both servers as they were found, then stopped.
down() {
  local id kind path left i
  [ "${KEEP:-0}" != 1 ] || { echo "KEEP=1: both servers are left as they are"; return 0; }
  set +e
  echo "== putting the servers back"
  if [ -f "$WORK/installed-s" ]; then
    plugin_on
    [ ! -f "$WORK/consumer-id" ] || req s DELETE "/JellyMesh/Admin/Servers/$(cat "$WORK/consumer-id")" >/dev/null
    docker exec "$S" sh -c "rm -rf /config/plugins/JellyMesh_$VERSION /config/jm-linked-off"
    restart s
    docker exec "$S" sh -c "rm -rf $DATA $XML $FED_S"
    rm -f "$WORK/installed-s" "$WORK/consumer-id"
  fi
  if [ -f "$WORK/installed-p" ]; then
    for kind in Film Serie; do
      req p DELETE "/Library/VirtualFolders?name=JM%20Linked%20$kind&refreshLibrary=false" >/dev/null
    done
    while IFS='|' read -r kind path; do
      req p DELETE "/Library/VirtualFolders/Paths?name=$kind&path=$path&refreshLibrary=false" >/dev/null
    done <"$WORK/paths"
    [ ! -f "$WORK/provider-id" ] || req p DELETE "/JellyMesh/Admin/Servers/$(cat "$WORK/provider-id")" >/dev/null
    id=$(user_id)
    [ -z "$id" ] || { req p DELETE "/JellyMesh/Admin/Peers/$id" >/dev/null; req p DELETE "/Users/$id" >/dev/null; }
    docker exec "$P" sh -c "rm -rf /config/plugins/JellyMesh_$VERSION"
    restart p
    docker exec "$P" sh -c "rm -rf $DATA $XML $FED_P"
    scan p
    left=$(sql p "select count(*) from BaseItems where Path like '$FED_P%';")
    check "items left under the federated folder" 0 "$left"
    rm -f "$WORK/installed-p" "$WORK/provider-id"
  fi
  for id in p s; do
    [ -f "$WORK/auth-$id" ] || continue
    [ ! -f "$WORK/libraries-$id" ] || check "$id libraries as found" "$(cat "$WORK/libraries-$id")" "$(libraries $id)"
    check "$id users named as the peer" 0 "$(get $id /Users | jq --arg n "$PEER" '[.[] | select(.Name == $n)] | length')"
  done
  for id in p s; do
    [ -f "$WORK/found-$id.tar" ] || { docker stop -t 60 "$(box $id)" >/dev/null; continue; }
    if [ "$(docker inspect -f '{{.State.Running}}' "$(box $id)")" = true ]; then
      scratch $id
      docker exec "$(box $id)" rm -f /tmp/jm-linked-start
      docker cp "$(box $id):/config" - >"$WORK/now-$id.tar"
      files "$WORK/found-$id.tar" "$WORK/now-$id.tar" extra | docker exec -i "$(box $id)" sh -c 'while read -r f; do rm -rf "/$f"; done'
      docker stop -t 60 "$(box $id)" >/dev/null
    fi
    # A server that did not close its database keeps a write-ahead log, which must not stay beside the old database.
    for i in 1 2 3; do
      docker cp "$(box $id):/config" - >"$WORK/now-$id.tar"
      tar -tf "$WORK/now-$id.tar" | grep -q -e '-wal$' || break
      up $id; docker stop -t 60 "$(box $id)" >/dev/null
    done
    docker cp -a - "$(box $id):/" <"$WORK/found-$id.tar"
    docker cp "$(box $id):/config" - >"$WORK/now-$id.tar"
    left=$(files "$WORK/found-$id.tar" "$WORK/now-$id.tar" check | paste -sd' ' -)
    check "$id /config as found, logs apart" "" "$left"
    if [ -z "$left" ]; then rm -f "$WORK/found-$id.tar" "$WORK/now-$id.tar"; else echo "the copy of /config of $id stays in $WORK/found-$id.tar"; fi
  done
  rm -rf "$WORK"/auth-* "$WORK/body" "$WORK/seen" "$WORK"/list-* "$WORK"/*.db "$WORK"/*.db-* "$WORK/index.jsonl" "$WORK/index.plain" "$WORK/written"
  [ ! -s "$WORK/results" ] || table
  if [ "$DONE" = 1 ] && [ "$FAILED" = 0 ] && [ "$LEAKS" = 0 ]; then echo "ALL PASS"; else echo "complete=$DONE failed controls=$FAILED leaks=$LEAKS"; fi
}

[ "${DOWN:-0}" != 1 ] || { down; exit 0; }
[ "$(docker inspect -f '{{.State.Running}}' "$P")$(docker inspect -f '{{.State.Running}}' "$S")" = falsefalse ] || { echo "the shared stack is in use" >&2; exit 1; }
! ls "$WORK"/found-*.tar >/dev/null 2>&1 || { echo "a kept run is in $WORK: put it back with DOWN=1" >&2; exit 1; }
rm -f "$WORK"/installed-* "$WORK"/*-id "$WORK"/libraries-* "$WORK"/auth-* "$WORK"/cursor "$WORK"/paths "$WORK"/results
umask 077
for id in p s; do docker cp "$(box $id):/config" - >"$WORK/found-$id.tar"; done
trap down EXIT
up s; memory
up p; memory
for id in p s; do docker exec "$(box $id)" touch /tmp/jm-linked-start; done
for id in p s; do heads $id >"$WORK/heads-$id"; done
# A file both servers have says nothing about who served it.
comm -23 "$WORK/heads-s" "$WORK/heads-p" >"$WORK/heads-x"
echo "video files with a head of their own: $(wc -l <"$WORK/heads-x" | tr -d ' ') of s, $(wc -l <"$WORK/heads-p" | tr -d ' ') of p"
[ -s "$WORK/heads-x" ] && [ -s "$WORK/heads-p" ] || { echo "the files of the servers cannot be told apart" >&2; exit 1; }
libraries p >"$WORK/libraries-p"
libraries s >"$WORK/libraries-s"
echo "== provider"
install p
settings p "$FED_P"
REMOTE=$(add_remote p Stock "$S_URL_INSIDE" jellymesh)
echo "$REMOTE" >"$WORK/provider-id"
sync_now p
check "p has no library made by the plugin" "$(cat "$WORK/libraries-p")" "$(libraries p)"
# The film and series folders of the server, one of each on 0.5 and one for each of its libraries on 0.6.
for kind in Film Serie; do
  docker exec "$P" find "$FED_P/$(dashed "$REMOTE")" -mindepth 1 -maxdepth 2 -type d -name "$kind" | sed "s/^/$kind|/"
done >"$WORK/paths"
[ "$(cut -d'|' -f1 "$WORK/paths" | sort -u | wc -l | tr -d ' ')" = 2 ] || { echo "the folders of the server are not there" >&2; exit 1; }
while IFS='|' read -r kind path; do
  post p "/Library/VirtualFolders/Paths?refreshLibrary=false" "$(jq -n --arg n "$kind" --arg p "$path" '{Name: $n, PathInfo: {Path: $p}}')" >/dev/null
  expect 204 "adding $path"
done <"$WORK/paths"
post p /Users/New "{\"Name\":\"$PEER\",\"Password\":\"jellymesh-test\"}" >/dev/null
expect 200 "making the peer user"
LAYOUT=setup
link_up || { echo "the stubs were not linked" >&2; exit 1; }
# The same user, before it is a peer, sees the stub version on a stock route.
peer_login
IFS='|' read -r stub real path <"$WORK/pairs"
mkdir -p "$WORK/seen"
peer before-sharing GET "/Items/$real"
check "the user sees both versions before it is a peer" true "$(jq --arg s "$stub" '[.MediaSources[].Id | ascii_downcase | gsub("-"; "")] | index($s) != null' "$SEEN")"
peer before-sharing-info POST "/Items/$real/PlaybackInfo?mediaSourceId=$stub" '{}'
check "and gets the stub version from PlaybackInfo" 200 "$CODE"
req p PUT "/JellyMesh/Admin/Peers/$(user_id)" "$(for n in Film Serie; do library_id "$n"; done | jq -R . | jq -s '{SharedLibraryIds: ., MaxStreams: 1000}')" >/dev/null
expect 204 "sharing"
peer_login
peer cursor GET /JellyMesh/Provider/Changes
jq -r .Cursor "$SEEN" >"$WORK/cursor"
layout shared

plugin_off
while IFS='|' read -r kind path; do
  req p DELETE "/Library/VirtualFolders/Paths?name=$kind&path=$path&refreshLibrary=false" >/dev/null
  expect 204 "removing $path"
done <"$WORK/paths"
for kind in Film Serie; do
  type=movies; [ "$kind" = Film ] || type=tvshows
  post p "/Library/VirtualFolders?name=JM%20Linked%20$kind&collectionType=$type&refreshLibrary=false$(sed -n "s/^$kind|/\&paths=/p" "$WORK/paths" | tr -d '\n')" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
  expect 204 "adding the library of $kind"
done
link_up || { echo "the stubs were not linked in their own libraries" >&2; exit 1; }
layout apart
DONE=1
[ "$FAILED" = 0 ] || exit 1
[ "$LEAKS" = 0 ] || exit 3
