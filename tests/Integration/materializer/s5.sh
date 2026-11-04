#!/usr/bin/env bash
# External subtitle marker: survives refreshes, stock PlaybackInfo still answers. Fresh up.sh.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder true
A=$(add_server Standard http://host.docker.internal:8097)
B=$(add_server JellyMeshRemote http://host.docker.internal:8098)
sync_and_scan
I=$(film_id "JM Audio e Sottotitoli")
show() { post "/Items/$I/PlaybackInfo?userId=$USER_ID" '{}' | jq -c '[.MediaSources[0].MediaStreams[]|{I:.Index,T:.Type,E:.IsExternal,P:.Path}]'; }
echo "after scan:"; show
echo "from the DB: $(sql_raw "select StreamIndex, IsExternal, Path from MediaStreamInfos where ItemId=upper('$I') or lower(ItemId)=lower('$I') order by StreamIndex;" | tr '\n' ' ')"
refresh_replace "$I"; sleep 5; scan; sleep 2
echo "after replace all and a scan:"; show
post "/Items/$I/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=Default" ; sleep 4
echo "after full refresh:"; show
echo "ffprobe on stub url ever: $(applog | grep -c 'ffprobe.*Play/[0-9a-f]\{32\}' || true); on the marker: $(applog | grep -c 'jellymesh-external-subtitle' || true)"
applog | grep -E "\[ERR\]" | grep -v -i "tmdb\|TheMovieDb" | tail -5 | cut -c1-200
