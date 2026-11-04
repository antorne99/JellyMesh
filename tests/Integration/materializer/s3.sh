#!/usr/bin/env bash
# Scenario 3, on a fresh up.sh with protection on: counters control, direct updates from a changed index, chapters.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
PLUGDIR=/config/plugins/Jellyfin.Plugin.JellyMesh
set_folder true
A=$(add_server Standard http://host.docker.internal:8097)
B=$(add_server JellyMeshRemote http://host.docker.internal:8098)
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
n=0; for id in $ids; do post "/Items/$id/PlaybackInfo?userId=$USER_ID" '{}' >/dev/null; n=$((n+1)); done; sleep 3
a1=$(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); b1=$(applog | grep -c 'Request starting.*JellyMesh/Play' || true)
echo "$n stubs: ffprobe +$((a1-a0)), requests +$((b1-b0))"

echo "### change the index by hand, as a remote edit would, then an incremental sync"
T=$(film_id "Tears of Steel")
before=$(get "/Users/$USER_ID/Items/$T" | jq -c '{Overview,Genres,Primary:.ImageTags.Primary,Chapters:(.Chapters|length)}')
echo "before: $before"
docker stop "$NAME" >/dev/null
docker cp "$NAME:$PLUGDIR/federation-index.json" "$D/index.json"
jq --arg name "Tears of Steel" '(.Servers[].Items[] | select(.Name==$name)) |= (.Overview="Trama cambiata dal remoto" | .Genres=["Genere Nuovo"] | .Etag="changed-etag" | .ImageTags.Primary="changedtag" | .Chapters=[{"Name":"Uno","StartPositionTicks":0},{"Name":"Due","StartPositionTicks":300000000}])' "$D/index.json" >"$D/index2.json"
docker cp "$D/index2.json" "$NAME:$PLUGDIR/federation-index.json"
docker start "$NAME" >/dev/null
wait_ready; sleep 5
login admin jellymesh-test
TID=$(get /ScheduledTasks | jq -r '.[]|select(.Key=="JellyMeshIncrementalSync")|.Id')
post "/ScheduledTasks/Running/$TID" >/dev/null
sleep 8
scan_settle
echo "after sync: $(get "/Users/$USER_ID/Items/$T" | jq -c '{Overview,Genres,Primary:.ImageTags.Primary,LockData}')"
docker exec "$NAME" sh -c 'grep -c "Trama cambiata" "/federata/Film/Tears of Steel (2012)/movie.nfo"; head -3 "/federata/Film/Tears of Steel (2012)/movie.nfo"; ls -la --time-style=+%H:%M:%S "/federata/Film/Tears of Steel (2012)"'
echo "chapters on the item: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters" | jq -c '[.Items[0].Chapters[]|{Name,StartPositionTicks}]')"
post "/Items/$T/PlaybackInfo?userId=$USER_ID" '{}' >/dev/null; sleep 2
echo "chapters after PlaybackInfo: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters" | jq -c '[.Items[0].Chapters[]|.Name]')"
refresh_replace "$T"; sleep 6; scan; sleep 2
echo "after replace all and a scan: $(get "/Items?userId=$USER_ID&ids=$T&Fields=Chapters,Overview,Genres" | jq -c '.Items[0]|{Overview,Genres,Run:.RunTimeTicks,Chapters:[.Chapters[]|.Name]}')"
echo "### nfo files not rewritten by Jellyfin"
docker exec "$NAME" sh -c 'cd /federata && grep -rl "<lockdata>false\|<dateadded>" --include=*.nfo . | wc -l'
echo "ffprobe stub url ever: $(applog | grep -c 'ffprobe.*Play/[0-9a-f]\{32\}' || true)"
