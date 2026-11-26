#!/usr/bin/env bash
# Scenario 2, on a fresh up.sh: Standard first, then JellyMesh (a second version, a new season), local key change,
# local arrivals with watch state for two users, decision 11 against ourselves, deselect and remove.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$D/lib.sh"
SRC=$MEDIA/remoto-standard
MARIO=$(get /Users | jq -r '.[]|select(.Name=="mario")|.Id')
set_folder true
A=$(add_server Standard "$STANDARD_URL")
sync_and_scan
echo "### Standard only"; series_view
EP5=$(ep_id "JM Serie Divisa" 1 5 /federata)
STRM5=$(docker exec "$NAME" cat "$(fed_file "/JM Serie Divisa S01E05 - Standard.strm")")
RUN5=$(get "/Users/$USER_ID/Items/$EP5" | jq -c '{Run:.RunTimeTicks,V:.MediaSources[0].MediaStreams|map(select(.Type=="Video"))[0].Height}')
post "/UserPlayedItems/$EP5?userId=$USER_ID" >/dev/null
echo "E05 $EP5 tech=$RUN5 state=$(user_data "$EP5")"
echo "### add JellyMesh: E05 gets a second version, Season 2 appears"
B=$(add_server JellyMeshRemote "$JELLYMESH_URL")
sync_and_scan
series_view
NEP5=$(ep_id "JM Serie Divisa" 1 5 /federata)
echo "E05 primary: $(get "/Users/$USER_ID/Items/$NEP5" | jq -r "$WHERE"'where') (same id as before: $([ "$EP5" = "$NEP5" ] && echo yes || echo no)), tech now: $(get "/Users/$USER_ID/Items/$NEP5" | jq -c '{Run:.RunTimeTicks,V:.MediaSources[0].MediaStreams|map(select(.Type=="Video"))[0].Height}')"
check "the stub of Standard keeps its file" "$STRM5" "$(docker exec "$NAME" cat "$(fed_file "/JM Serie Divisa S01E05 - Standard.strm")")"
check "E05 shows the versions of both servers" '["JellyMeshRemote","Standard"]' "$(get "/Users/$USER_ID/Items/$NEP5" | jq -c '[.MediaSources[].Name] | sort')"
check "E05 stays played once the versions join (row 63)" true "$(get "/Users/$USER_ID/Items/$NEP5" | jq .UserData.Played)"
echo "E05 state on the stub of Standard: $(user_data "$EP5"), on the primary: $(user_data "$NEP5")"
check "Season 2 appears" 2 "$(get "/Shows/$(items Series | jq -r '.Items[]|select(.Name=="JM Serie Divisa")|.Id')/Episodes?userId=$USER_ID&season=2" | jq '.Items | length')"
echo "stubs of E05 and their sources: $(jm_sql "select e.relative_path || ' <- ' || group_concat(substr(s.server_id, 1, 6)) from stub_entries e join stub_entry_sources s on s.stub_id = e.id where e.episode_number = 5 and e.relative_path like '%Divisa%' group by e.id;" | paste -sd ' ' -)"
echo "server ids: A=${A:0:6} B=${B:0:6}"

echo "### local key change: Custom on the local Serie Senza ID"
LS=$(sql_raw "select lower(Id) from BaseItems where Type like '%TV.Series' and Path='/media/serie/Serie Senza ID (2026)';")
dto=$(get "/Users/$USER_ID/Items/$LS" | jq -c '.ProviderIds={"Custom":"jmcustom1"}')
post "/Items/$LS" "$dto"
sync_and_scan
sql_raw "select substr(Id,1,8), Path, PresentationUniqueKey from BaseItems where Type like '%TV.Series' and Path like '%Senza ID%';"
docker exec "$NAME" sh -c 'find /federata -path "*/Serie Senza ID (2026)/tvshow.nfo" -exec cat {} \; -exec echo \;'
series_view

echo "### watch state on stubs"
set_state() { # <item> <user> <json>
  post "/UserItems/$1/UserData?userId=$2" "$3" >/dev/null
}
T=$(film_id "Tears of Steel"); S=$(film_id "Sonda Senza ID"); E4=$(ep_id "JM Serie Divisa" 1 4 /federata); E2=$(ep_id "Serie Senza ID" 1 2 /federata)
SS=$(sql_raw "select lower(Id) from BaseItems where Type like '%TV.Series' and Path like '/federata/%/Serie Senza ID (2026)' limit 1;")
set_state "$T" "$USER_ID" '{"Played":true,"PlayCount":2,"LastPlayedDate":"2026-10-01T10:00:00Z"}'
set_state "$T" "$MARIO" '{"Played":false,"PlaybackPositionTicks":123450000,"LastPlayedDate":"2026-10-02T10:00:00Z"}'
set_state "$S" "$USER_ID" '{"Played":false,"PlaybackPositionTicks":223450000,"LastPlayedDate":"2026-10-03T10:00:00Z"}'
set_state "$S" "$MARIO" '{"Played":true,"PlayCount":1,"LastPlayedDate":"2026-10-04T10:00:00Z","IsFavorite":true}'
set_state "$E4" "$USER_ID" '{"Played":true,"PlayCount":1,"LastPlayedDate":"2026-10-05T10:00:00Z"}'
set_state "$E4" "$MARIO" '{"Played":false,"PlaybackPositionTicks":103450000,"LastPlayedDate":"2026-10-06T10:00:00Z"}'
set_state "$E2" "$USER_ID" '{"Played":false,"PlaybackPositionTicks":153450000,"LastPlayedDate":"2026-10-07T10:00:00Z"}'
set_state "$E2" "$MARIO" '{"Played":true,"PlayCount":3,"LastPlayedDate":"2026-10-08T10:00:00Z"}'
set_state "$SS" "$MARIO" '{"IsFavorite":true}'
for i in "$T" "$S" "$E4" "$E2"; do echo "stub $i admin=$(user_data "$i") mario=$(user_data "$i" "$MARIO")"; done

echo "### local copies arrive: the stubs stay and the local copy becomes the primary (row 64)"
stub_sum() { echo "$(stub_files | grep -c .) stubs, checksum $(stub_files | cksum | cut -d' ' -f1)"; }
stubs_before=$(stub_sum)
docker exec "$NAME" sh -c 'mkdir -p "/local-extra/film/Tears of Steel (2012)" "/local-extra/film/Sonda Senza ID (2026)" "/local-extra/serie/JM Serie Divisa (2026)/Season 01" "/local-extra/serie/Serie Senza ID (2026)/Season 01"'
docker cp "$SRC/film/Tears of Steel (2012)/." "$NAME:/local-extra/film/Tears of Steel (2012)/"
docker cp "$SRC/film/Sonda Senza ID (2026)/." "$NAME:/local-extra/film/Sonda Senza ID (2026)/"
docker cp "$SRC/serie/JM Serie Divisa (2026)/tvshow.nfo" "$NAME:/local-extra/serie/JM Serie Divisa (2026)/tvshow.nfo"
docker cp "$SRC/serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E04.mkv" "$NAME:/local-extra/serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E04.mkv"
docker cp "$SRC/serie/Serie Senza ID (2026)/Season 01/Serie Senza ID S01E02.mkv" "$NAME:/local-extra/serie/Serie Senza ID (2026)/Season 01/Serie Senza ID S01E02.mkv"
docker exec "$NAME" sh -c 'ls -R /local-extra | head -30'
scan
sleep 4
sync_and_scan
check "every stub stays" "$stubs_before" "$(stub_sum)"
echo "### results"
items Movie | jq -c '.Items[] | select(.Name=="Tears of Steel" or (.Name|startswith("Sonda Senza"))) | {Id:.Id[0:8],Name,Path}'
NT=$(film_id "Tears of Steel"); NS=$(items Movie | jq -r '.Items[] | select(.Name | startswith("Sonda Senza ID")) | .Id' | head -1)
NE4=$(ep_id "JM Serie Divisa" 1 4); NE2=$(ep_id "Serie Senza ID" 1 2)
for i in "$NT" "$NS" "$NE4" "$NE2"; do echo "primary $(get "/Users/$USER_ID/Items/$i" | jq -r .Path)"; echo "  admin=$(user_data "$i") mario=$(user_data "$i" "$MARIO")"; done
check "the local copies are the primary" "local local local local" \
  "$(for i in "$NT" "$NS" "$NE4" "$NE2"; do get "/Users/$USER_ID/Items/$i" | jq -r "$WHERE"'where'; done | paste -sd ' ' -)"
check "played reaches the local primary (rows 63, 64)" "true true true true" \
  "$(echo "$(get "/Users/$USER_ID/Items/$NT" | jq .UserData.Played) $(get "/Users/$MARIO/Items/$NS" | jq .UserData.Played) $(get "/Users/$USER_ID/Items/$NE4" | jq .UserData.Played) $(get "/Users/$MARIO/Items/$NE2" | jq .UserData.Played)")"
series_view
echo "### rows"
sql_raw "select substr(Id,1,8), replace(Type,'MediaBrowser.Controller.Entities.',''), Path from BaseItems where Path like '/federata/%' order by Path;"
echo "mario favorites on series: $(curl -fsS -H "$ACCEPT" -H "$(auth_header)" "$BASE/Items?userId=$MARIO&Recursive=true&IncludeItemTypes=Series&Filters=IsFavorite" | jq -c '[.Items[]|.Name]')"
echo "### another sync keeps the stubs"
sync_and_scan
check "every stub stays after a sync" "$stubs_before" "$(stub_sum)"
echo "### the local film goes away: a stub is the primary again"
docker exec "$NAME" sh -c 'rm -rf "/local-extra/film/Tears of Steel (2012)"'
scan; sync_and_scan
stub_files | grep Tears
items Movie | jq -c '.Items[] | select(.Name=="Tears of Steel") | {Id:.Id[0:8],Path}'
check "Tears of Steel is a stub again" Standard "$(get "/Users/$USER_ID/Items/$(film_id "Tears of Steel")" | jq -r "$WHERE"'where')"
echo "### decision 11: this server as its own remote"
C=$(add_server Self http://127.0.0.1:8096 mario)
sync_and_scan
get /JellyMesh/Admin/Sync | jq -c '.Servers[]|{ServerName,State,LastFailure,Movies}'
get /JellyMesh/Admin/Index/Decisions | jq -c --arg c "$C" '{Self_sources:([.Contents[] | (.RemoteItems[]) | select(.ServerId==($c|.[0:8]+"-"+.[8:12]+"-"+.[12:16]+"-"+.[16:20]+"-"+.[20:]))] | length), Self_winner_or_backup:([.Contents[] | ([.Winner]+.Backups)[] | select(.!=null) | select(.ServerName=="Self") | .Title])}'
echo "stub files of Self: $(stub_files | grep -c -- ' - Self\.strm' || true)"
jm_sql "select relative_path || ' ' || work_key from stub_series order by relative_path;"
HIDDEN=$(sql_raw "select lower(Id) from BaseItems where Type like '%TV.Series' and Path='/media/serie/JM Serie Divisa (2026)';")
login mario jellymesh-test
M=$(get /Users/Me | jq -r .Id)
echo "lookup of the collapsed local row by ids, as a non admin: $(get "/Items?userId=$M&recursive=true&includeItemTypes=Series&ids=$HIDDEN&fields=ProviderIds" | jq -c '{T:.TotalRecordCount,N:[.Items[]|.Path//.Name]}')"
echo "without includeItemTypes: $(get "/Items?userId=$M&ids=$HIDDEN&fields=ProviderIds" | jq -c '{T:.TotalRecordCount,N:[.Items[]|.Name]}')"
echo "by /Users/{id}/Items/{id}: $(get "/Users/$M/Items/$HIDDEN" | jq -c '{Name,ProviderIds}')"
login admin jellymesh-test
curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/JellyMesh/Admin/Servers/$C" >/dev/null
unlink_server "$C"
echo "### deselect the film library of JellyMesh and of Standard: film stubs go"
film_before=$(stub_files | grep -c /Film/ || true)
for s in "$A" "$B"; do
  choose_libraries "$s" '[.Libraries[] | select(.CollectionType == "tvshows") | .Id]'
done
sync_and_scan
check "film stubs after the deselection" "$film_before 0" "$film_before $(stub_files | grep -c /Film/ || true)"
docker exec "$NAME" sh -c 'find /federata -maxdepth 3 -name Film'
echo "### remove both servers: everything goes, then the admin takes their folders out of the libraries"
curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/JellyMesh/Admin/Servers/$A" >/dev/null
curl -fsS -X DELETE -H "$ACCEPT" -H "$(auth_header)" "$BASE/JellyMesh/Admin/Servers/$B" >/dev/null
sync_and_scan
check "no stub left" 0 "$(stub_files | grep -c . || true)"
unlink_server "$A"; unlink_server "$B"
scan
docker exec "$NAME" sh -c 'cd /federata && find . | sort'
check "no item left in the federated folder" 0 "$(sql_raw "select count(*) from BaseItems where Path like '/federata/%';")"
series_view
get /JellyMesh/Admin/Sync | jq -c .Servers
applog | grep -E "\[ERR\]|\[WRN\]" | grep -v -i "tmdb\|TheMovieDb\|Tvdb" | tail -12 | cut -c1-220 || true
echo "ffprobe on stub url ever: $(applog | grep -c 'ffprobe.*JellyMesh/Play' || true); requests to target: $(applog | grep -c 'Request starting.*JellyMesh/Play' || true)"
exit "${FAILED:-0}"
