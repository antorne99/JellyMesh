#!/usr/bin/env bash
# Scenario 4, on a fresh up.sh with protection off: thin .nfo, numbers survive replace all, NextUp, no ffprobe.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder false
A=$(add_server Standard http://host.docker.internal:8097)
B=$(add_server JellyMeshRemote http://host.docker.internal:8098)
sync_and_scan
docker exec "$NAME" sh -c 'cat "/federata/Film/Tears of Steel (2012)/movie.nfo"; echo; cat "/federata/Serie/JM Serie Divisa (2026)/tvshow.nfo"; echo; cat "/federata/Serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E04.nfo"; echo'
series_view
E4=$(ep_id "JM Serie Divisa" 1 4 /federata)
echo "E4: $(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length)}')"
refresh_replace "$E4"; refresh_replace "$(ep_id "JM Serie Divisa" 2 1 /federata)"; sleep 6; scan; sleep 2
echo "E4 after replace all: $(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length)}')"
nextup_walk "JM Serie Divisa"
unplay_all
echo "### protection back on: items get locked directly"
set_folder true
sync_and_scan
echo "E4: $(get "/Users/$USER_ID/Items/$E4" | jq -c '{LockData}') nfo lockdata: $(docker exec "$NAME" sh -c 'grep -c lockdata "/federata/Serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E04.nfo"')"
echo "ffprobe stub url ever: $(applog | grep -c 'ffprobe.*Play/[0-9a-f]\{32\}' || true); requests: $(applog | grep -c 'Request starting.*JellyMesh/Play' || true)"
applog | grep -E "\[ERR\]" | grep -v -i "tmdb\|TheMovieDb" | tail -5 | cut -c1-220
