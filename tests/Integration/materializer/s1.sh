#!/usr/bin/env bash
# Scenario 1 (continues the current instance is NOT assumed): needs a fresh up.sh.
# JellyMesh remote first, then Standard: S01E04 arrives later in an existing stub season.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder true
A=$(add_server JellyMeshRemote http://host.docker.internal:8098)
sync_and_scan
echo "### after first remote"; stub_files | grep Serie; series_view
echo "NeedsRefresh left: $(docker exec "$NAME" sh -c 'cat /config/plugins/configurations/*/federated-stubs.json /config/data/plugins/*/federated-stubs.json 2>/dev/null' | jq -c '[.Series[]|.NeedsRefresh]')"
docker exec "$NAME" sh -c 'find /config -name federated-stubs.json'
EP5=$(ep_id "JM Serie Divisa" 1 5 /federata)
FILM=$(film_id "Sonda Senza ID")
FILM_PATH=$(items Movie | jq -r --arg i "$FILM" '.Items[]|select(.Id==$i)|.Path')
echo "E05 id=$EP5 film=$FILM $FILM_PATH"
post "/UserPlayedItems/$FILM?userId=$USER_ID" >/dev/null
echo "film state before: $(user_data "$FILM")"
echo "### add Standard: E04 arrives later"
B=$(add_server Standard http://host.docker.internal:8097)
sync_and_scan
stub_files | grep -E "Serie|Sonda"
series_view
echo "E05 id after: $(ep_id "JM Serie Divisa" 1 5 /federata) (same=$([ "$EP5" = "$(ep_id "JM Serie Divisa" 1 5 /federata)" ] && echo yes || echo no))"
echo "film id after: $(film_id "Sonda Senza ID") state: $(user_data "$(film_id "Sonda Senza ID")")"
echo "### E04 row"
sql_raw "select Path, ParentIndexNumber, IndexNumber, IsLocked, Name from BaseItems where Type like '%Episode' and Path like '/federata/%' order by ParentIndexNumber, IndexNumber;"
echo "### series rows"
sql_raw "select substr(Id,1,8), replace(Type,'MediaBrowser.Controller.Entities.',''), Path, IndexNumber, IsLocked, PresentationUniqueKey from BaseItems where Type like '%TV.Series' or Type like '%TV.Season' order by Type desc, Path;"
echo "### NextUp walk"
nextup_walk "JM Serie Divisa"
unplay_all
echo "### replace all metadata on an episode and the series"
SER=$(items Series | jq -r '.Items[]|select(.Name=="JM Serie Divisa")|.Id')
E4=$(ep_id "JM Serie Divisa" 1 4 /federata)
before=$(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks}')
refresh_replace "$E4"; refresh_replace "$SER"; refresh_replace "$(film_id 'Tears of Steel')"
sleep 8; scan; sleep 2
echo "before: $before"
echo "after : $(get "/Users/$USER_ID/Items/$E4" | jq -c '{Name,S:.ParentIndexNumber,E:.IndexNumber,LockData,Run:.RunTimeTicks}')"
get "/Users/$USER_ID/Items/$(film_id 'Tears of Steel')" | jq -c '{Name,Overview,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length),Genres}'
series_view
echo "ffprobe on stub url ever: $(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); requests to target: $(applog | grep -c 'Request starting.*JellyMesh/Play' || true)"
