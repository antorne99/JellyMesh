#!/usr/bin/env bash
# Scenario 1 (continues the current instance is NOT assumed): needs a fresh up.sh.
# JellyMesh remote first, then Standard: S01E04 arrives later, in the folder of Standard, next to the stubs of the first.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
set_folder true
A=$(add_server JellyMeshRemote "$JELLYMESH_URL")
sync_and_scan
echo "### after first remote"; stub_files | grep Serie; series_view
echo "NeedsRefresh left: $(jm_sql 'select needs_refresh from stub_series order by relative_path;' | paste -sd, -)"
EP5=$(ep_id "JM Serie Divisa" 1 5 /federata)
post "/UserPlayedItems/$EP5?userId=$USER_ID" >/dev/null
FILM=$(film_id "Sonda Senza ID")
FILM_PATH=$(items Movie | jq -r --arg i "$FILM" '.Items[]|select(.Id==$i)|.Path')
echo "E05 id=$EP5 film=$FILM $FILM_PATH"
post "/UserPlayedItems/$FILM?userId=$USER_ID" >/dev/null
echo "film state before: $(user_data "$FILM")"
echo "### add Standard: E04 arrives later"
B=$(add_server Standard "$STANDARD_URL")
sync_and_scan
stub_files | grep -E "Serie|Sonda"
series_view
check "E04 of Standard has its stub with the label" 1 "$(stub_files | grep -c 'JM Serie Divisa S01E04 - Standard.strm')"
check "the client sees E04" 1 "$(items Episode | jq '[.Items[] | select(.SeriesName == "JM Serie Divisa" and .ParentIndexNumber == 1 and .IndexNumber == 4)] | length')"
check "the client sees one E05 with the versions of both servers" '1 ["JellyMeshRemote","Standard"]' \
  "$(items Episode | jq -c '[.Items[] | select(.SeriesName == "JM Serie Divisa" and .ParentIndexNumber == 1 and .IndexNumber == 5)] | "\(length) \([.[0].MediaSources[].Name] | sort | tojson)"' -r)"
check "E05 stays played once the versions join (row 63)" true "$(get "/Users/$USER_ID/Items/$(ep_id "JM Serie Divisa" 1 5 /federata)" | jq .UserData.Played)"
check "the film keeps its state" true "$(get "/Users/$USER_ID/Items/$(film_id "Sonda Senza ID")" | jq .UserData.Played)"
echo "E05 id after: $(ep_id "JM Serie Divisa" 1 5 /federata) (same=$([ "$EP5" = "$(ep_id "JM Serie Divisa" 1 5 /federata)" ] && echo yes || echo no))"
echo "film id after: $(film_id "Sonda Senza ID") state: $(user_data "$(film_id "Sonda Senza ID")")"
echo "### E04 row"
sql_raw "select Path, ParentIndexNumber, IndexNumber, IsLocked, Name from BaseItems where Type like '%Episode' and Path like '/federata/%' order by ParentIndexNumber, IndexNumber;"
echo "### series rows"
sql_raw "select substr(Id,1,8), replace(Type,'MediaBrowser.Controller.Entities.',''), Path, IndexNumber, IsLocked, PresentationUniqueKey from BaseItems where Type like '%TV.Series' or Type like '%TV.Season' order by Type desc, Path;"
echo "### NextUp walk"
unplay_all
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
check "replace all keeps the numbers of the locked stub" "$(jq -c '{S,E,LockData}' <<<"$before")" "$(get "/Users/$USER_ID/Items/$E4" | jq -c '{S:.ParentIndexNumber,E:.IndexNumber,LockData}')"
get "/Users/$USER_ID/Items/$(film_id 'Tears of Steel')" | jq -c '{Name,Overview,LockData,Run:.RunTimeTicks,N:(.MediaSources[0].MediaStreams|length),Genres}'
series_view
echo "ffprobe on stub url ever: $(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); requests to target: $(applog | grep -c 'Request starting.*JellyMesh/Play' || true)"
exit "${FAILED:-0}"
