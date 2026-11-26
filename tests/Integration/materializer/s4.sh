#!/usr/bin/env bash
# Scenario 4, on a fresh up.sh with protection off: thin .nfo, numbers survive replace all, NextUp, no ffprobe.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder false
A=$(add_server Standard "$STANDARD_URL")
B=$(add_server JellyMeshRemote "$JELLYMESH_URL")
sync_and_scan
E4NFO=$(fed_file "/JM Serie Divisa S01E04 - Standard.nfo")
for f in "$(fed_file "/Tears of Steel (2012)/movie.nfo")" "$(dirname "$(dirname "$E4NFO")")/tvshow.nfo" "$E4NFO"; do docker exec "$NAME" cat "$f"; echo; done
series_view
E4=$(ep_id "JM Serie Divisa" 1 4 /federata)
echo "E4: $(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length)}')"
refresh_replace "$E4"; refresh_replace "$(ep_id "JM Serie Divisa" 2 1 /federata)"; sleep 6; scan; sleep 2
echo "E4 after replace all: $(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length)}')"
check "replace all keeps the numbers of the unlocked stub" '{"S":1,"E":4}' "$(get "/Users/$USER_ID/Items/$E4" | jq -c '{S:.ParentIndexNumber,E:.IndexNumber}')"
nextup_walk "JM Serie Divisa"
unplay_all
echo "### protection back on: items get locked directly"
set_folder true
sync_and_scan
check "protection on: the stub is locked, and its nfo" '{"LockData":true} 1' "$(get "/Users/$USER_ID/Items/$E4" | jq -c '{LockData}') $(docker exec "$NAME" grep -c '<lockdata>true</lockdata>' "$E4NFO")"
echo "ffprobe stub url ever: $(applog | grep -c 'ffprobe.*Play/[0-9a-f]\{32\}' || true); requests: $(applog | grep -c 'Request starting.*JellyMesh/Play' || true)"
applog | grep -E "\[ERR\]" | grep -v -i "tmdb\|TheMovieDb" | tail -5 | cut -c1-220 || true
exit "${FAILED:-0}"
