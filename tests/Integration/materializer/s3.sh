#!/usr/bin/env bash
# Scenario 3, on a fresh up.sh with protection on: counters control, an edit on the Standard remote reaching the stub
# through an incremental sync, chapters. The edit is put back on exit.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder true
A=$(add_server Standard "$STANDARD_URL")
B=$(add_server JellyMeshRemote "$JELLYMESH_URL")
sync_and_scan
echo "### .nfo hashes before anything"
hashes() { docker exec "$NAME" sh -c 'cd /federata && find . -type f ! -name "*.jpg" | sort | xargs -d "\n" md5sum | md5sum'; }
H0=$(hashes); echo "$H0"

echo "### control: a .strm outside the federated folder IS probed, so the counters work"
docker exec "$NAME" sh -c 'mkdir -p "/local-extra/film/Controllo (2026)" && echo "http://127.0.0.1:8096/JellyMesh/Play/control00000000000000000000000000" > "/local-extra/film/Controllo (2026)/Controllo (2026).strm"'
scan
C=$(items Movie | jq -r '.Items[]|select(.Path|contains("Controllo"))|.Id')
a0=$(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); b0=$(applog | grep -c 'Request starting.*JellyMesh/Play' || true)
post "/Items/$C/PlaybackInfo?userId=$USER_ID" '{}' >/dev/null; sleep 3
a1=$(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); b1=$(applog | grep -c 'Request starting.*JellyMesh/Play' || true)
echo "control PlaybackInfo: ffprobe lines +$((a1-a0)), requests +$((b1-b0))"
docker exec "$NAME" sh -c 'rm -rf "/local-extra/film/Controllo (2026)"'
scan

echo "### stubs again: counters stand still"
a0=$(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); b0=$(applog | grep -c 'Request starting.*JellyMesh/Play' || true)
ids=$(items Movie,Episode | jq -r '.Items[]|select(.Path|startswith("/federata"))|.Id')
# A JellyMesh remote may refuse some with 503 once its stream limit for this server is reached: only probes count here.
n=0 refused=0
for id in $ids; do
  [ "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "$ACCEPT" -H "$(auth_header)" -H 'Content-Type: application/json' -d '{}' "$BASE/Items/$id/PlaybackInfo?userId=$USER_ID")" = 200 ] || refused=$((refused+1))
  n=$((n+1))
done
sleep 3
a1=$(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); b1=$(applog | grep -c 'Request starting.*JellyMesh/Play' || true)
echo "$n stubs ($refused refused): ffprobe +$((a1-a0)), requests +$((b1-b0))"
check "no probe of a stub" 0 "$((a1-a0))"

echo "### the Standard remote edits a film, then an incremental sync"
T=$(film_id "Tears of Steel")
before=$(get "/Users/$USER_ID/Items/$T" | jq -c '{Overview,Genres,Primary:.ImageTags.Primary,Chapters:(.Chapters|length)}')
echo "before: $before"
if [ "$STANDARD_URL_GIVEN" != yes ]; then
  echo "skipped: the edit changes the remote, so it runs only on a remote named in STANDARD_URL"
else
  RUID=$(remote GET /Users/Me | jq -r .Id)
  RT=$(remote GET "/Items?userId=$RUID&Recursive=true&IncludeItemTypes=Movie&searchTerm=Tears" | jq -r '.Items[] | select(.Name == "Tears of Steel") | .Id')
  remote GET "/Users/$RUID/Items/$RT" >"${TMPDIR:-/tmp}/$NAME-remote-item.json"
  restore() { remote POST "/Items/$RT" "$(cat "${TMPDIR:-/tmp}/$NAME-remote-item.json")" >/dev/null; }
  trap restore EXIT
  remote POST "/Items/$RT" "$(jq -c '.Overview = "Trama cambiata dal remoto" | .Genres = ["Genere Nuovo"]' "${TMPDIR:-/tmp}/$NAME-remote-item.json")" >/dev/null
  TID=$(get /ScheduledTasks | jq -r '.[]|select(.Key=="JellyMeshIncrementalSync")|.Id')
  post "/ScheduledTasks/Running/$TID" >/dev/null
  sleep 8
  scan_settle
  echo "after sync: $(get "/Users/$USER_ID/Items/$T" | jq -c '{Overview,Genres,Primary:.ImageTags.Primary,LockData}')"
  check "the edit of the remote reaches the stub" '{"Overview":"Trama cambiata dal remoto","Genres":["Genere Nuovo"]}' "$(get "/Users/$USER_ID/Items/$T" | jq -c '{Overview,Genres}')"
  NFO=$(fed_file "/Tears of Steel (2012)/movie.nfo")
  check "and its nfo" 1 "$(docker exec "$NAME" grep -c "Trama cambiata" "$NFO")"
  docker exec "$NAME" sh -c 'head -3 "$1"; ls -la --time-style=+%H:%M:%S "$(dirname "$1")"' sh "$NFO"
fi
echo "chapters on the item: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters" | jq -c '[.Items[0].Chapters[]|{Name,StartPositionTicks}]')"
post "/Items/$T/PlaybackInfo?userId=$USER_ID" '{}' >/dev/null; sleep 2
echo "chapters after PlaybackInfo: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters" | jq -c '[.Items[0].Chapters[]|.Name]')"
refresh_replace "$T"; sleep 6; scan; sleep 2
echo "after replace all and a scan: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters,Overview,Genres" | jq -c '.Items[0]|{Overview,Genres,Run:.RunTimeTicks,Chapters:[.Chapters[]|.Name]}')"
echo "### nfo files not rewritten by Jellyfin"
docker exec "$NAME" sh -c 'cd /federata && grep -rl "<lockdata>false\|<dateadded>" --include=*.nfo . | wc -l'
echo "ffprobe stub url ever: $(applog | grep -c 'ffprobe.*Play/[0-9a-f]\{32\}' || true)"
exit "${FAILED:-0}"
