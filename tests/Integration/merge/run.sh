#!/usr/bin/env bash
# Manual merges on the per-server layout (decision 20, Q23): copies of one server keep one stub, which takes the state of
# the other; copies of different servers and a local copy become versions of one group, linked at once, and the titles
# that join in one library give their state to the primary, position aside (decision 172); an undo dissolves the group. Also a new stub linked right after the scan that makes its item (Q145). Needs up.sh. Run once.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
uid=$(get local /Users/Me | jq -r .Id)


echo "--- the list"
list local | jq -c '.Cases[] | {Kind, Titles: [.Titles[] | [.Copies[] | "\(.Copy.Title) \(.ServerName // "local")"]]}'
check "the chain is one case of three titles" 3 "$(list local | jq '[.Cases[] | select(any(.Titles[].Copies[]; .Copy.Title == "JM Catena Uno")) | .Titles[]] | length')"
check "copies say where they live" '["peer","remote","remote"]' "$(list local | jq -c '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena")) | .ServerName] | sort')"
check "two local films are not listed" 0 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title == "JM Due Locali")] | length')"
check "the list holds no address" 0 "$(list local | grep -c "host.docker.internal\|:$((PORT + 1))\|:$((PORT + 2))" || true)"
login local jellymesh
mv "$WORK/token-local" "$WORK/token-user"
login local
check "a user without admin rights gets no list" 403 "$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(cat "$WORK/token-user")\"" "http://127.0.0.1:$PORT/JellyMesh/Admin/Merges")"
check "nor a merge" 403 "$(curl -sS -o /dev/null -w '%{http_code}' -X POST -H "Authorization: $AUTH, Token=\"$(cat "$WORK/token-user")\"" -H 'Content-Type: application/json' -d '{"WorkKeys":["a","b"]}' "http://127.0.0.1:$PORT/JellyMesh/Admin/Merges")"

echo "--- merging the chain: Uno and Tre on remote become one stub, Due on peer a version of it"
uno=$(film_of remote "JM Catena Uno"); tre=$(film_of remote "JM Catena Tre"); due=$(film_of peer "JM Catena Due")
check "three stubs, none linked" "3 ::" "$(strm_count 'JM Catena') $(primary_of "$uno")$(primary_of "$due"):$(alternates_of "$uno")$(alternates_of "$due"):"
post local "/UserPlayedItems/$(dashed "$tre")?userId=$uid" >/dev/null
post local "/UserFavoriteItems/$(dashed "$tre")?userId=$uid" >/dev/null
check "one title cannot be merged" 400 "$(post local /JellyMesh/Admin/Merges '{"WorkKeys":["only one"]}' >/dev/null; status)"
check "the merge is accepted" 204 "$(merge local '^JM Catena')"
check "the copies of two servers are linked at once, the best as primary" "$due:$uno" "$(group "$uno")"
applied local
check "remote keeps one stub, peer its own" "1 1" "$(docker exec "$PREFIX-local" sh -c "find '$(server_folder "$(cat "$WORK/id-remote")")/Film' -name '*.strm' | grep -c 'JM Catena'") $(docker exec "$PREFIX-local" sh -c "find '$(server_folder "$(cat "$WORK/id-peer")")/Film' -name '*.strm' | grep -c 'JM Catena'")"
check "the stub of the lowest key stayed, as the same item" "$uno" "$(film_of remote "JM Catena Uno")"
check "the merged stub is gone" "" "$(film_of remote "JM Catena Tre")"
check "the stub that stayed has the state of the merged one (decision 2)" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$uno")"
check "the group is still the two versions" "$due:$uno" "$(group "$uno")"
# Both server folders are in one library, so the titles that joined give their state to the primary (decision 172).
check "the primary takes favorite and play count of the titles that joined" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$due")"
check "played is one for the title (Q42)" true "$(state "$due" | jq .Played)"
check "one row for the client" 1 "$(rows 'JM Catena')"
check "the decision is manual" Manual "$(get local /JellyMesh/Admin/Index/Decisions | jq -r '[.Contents[] | select(.Identity.Key | contains("999100001")) | .Identity.Match] | first')"
check "the case is gone" 0 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena"))] | length')"
check "the primary offers both versions" 2 "$(get local "/Items/$(dashed "$due")?userId=$uid" | jq '.MediaSources | length')"
info=$(post local "/Items/$(dashed "$due")/PlaybackInfo?userId=$uid" "$DIRECT")
code=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(token_of local)\"" -H "Range: bytes=0-1023" \
  "http://127.0.0.1:$PORT/Videos/$(dashed "$due")/stream?static=true&mediaSourceId=$(echo "$info" | jq -r '.MediaSources[0].Id')")
check "and plays" 206 "$code"
check "no remote address in PlaybackInfo" 0 "$(echo "$info" | grep -c "host.docker.internal\|:$((PORT + 1))\|:$((PORT + 2))" || true)"

echo "--- merging the pair without IDs, one copy on each server: no scan to wait for"
mob_a=$(film_of remote "JM Sonda Mobile"); mob_b=$(film_of peer "JM Sonda Mobile")
check "the pair is listed" 2 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title == "JM Sonda Mobile")] | length')"
check "the merge is accepted" 204 "$(merge local '^JM Sonda Mobile$')"
check "linked before any scan or sync" "$mob_b:$mob_a" "$(group "$mob_a")"
check "both stubs stay" 2 "$(strm_count 'JM Sonda Mobile')"

echo "--- a restart and two syncs"
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "the merges are back" 2 "$(list local | jq '.Merges | length')"
check "and in effect" true "$(list local | jq '[.Merges[].IsInEffect] | all')"
sync_now local; scan local; sync_now local
check "the merged stub did not come back" 2 "$(strm_count 'JM Catena')"
check "the groups hold" "$due:$uno $mob_b:$mob_a" "$(group "$uno") $(group "$mob_a")"
check "the watch state stayed" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$uno")"

echo "--- a copy gains IDs"
peer_admin=$(get peer /Users/Me | jq -r .Id)
peer_item=$(get peer '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r '.Items[] | select(.Path | contains("JM Sonda Mobile")) | .Id')
docker exec "$PREFIX-peer" sh -c "sed -i 's#</movie>#<uniqueid type=\"tmdb\" default=\"true\">999100021</uniqueid></movie>#' '/extra/film/JM Sonda Mobile (2026)/movie.nfo'"
post peer "/Items/$peer_item/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=false&replaceAllImages=false" >/dev/null
sleep 5
check "the remote has the ID" 999100021 "$(get peer "/Users/$peer_admin/Items/$peer_item" | jq -r .ProviderIds.Tmdb)"
sync_now local; settle local; scan local
check "the merge holds" true "$(list local | jq --arg i "$(merge_id local '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "and so does the group" "$mob_b:$mob_a" "$(group "$mob_a")"

echo "--- a copy loses its year: \"Replace all metadata\" on a remote does that"
post peer "/Items/$peer_item/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false" >/dev/null
sleep 5
check "the remote has no year" null "$(get peer "/Users/$peer_admin/Items/$peer_item" | jq -r .ProductionYear)"
sync_now local; settle local; scan local
check "the merge holds" true "$(list local | jq --arg i "$(merge_id local '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "both stubs stay" 2 "$(strm_count 'JM Sonda Mobile')"
mob_b=$(film_of peer "JM Sonda Mobile")
check "and the group holds" "$mob_b:$mob_a" "$(group "$mob_a")"

echo "--- a copy disappears, and comes back"
docker exec "$PREFIX-remote" mv '/extra/film/JM Sonda Mobile (2026)' /extra/parked
scan remote
sync_now local; settle local; scan local
check "the merge waits" false "$(list local | jq --arg i "$(merge_id local '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "for the copy that is missing" 1 "$(list local | jq --arg i "$(merge_id local '^JM Sonda Mobile$')" '[.Merges[] | select(.Id == $i) | .Copies[] | select(.IsFound | not)] | length')"
check "the title stays, with the stub of peer" "1 1" "$(rows 'JM Sonda Mobile') $(strm_count 'JM Sonda Mobile')"
docker exec "$PREFIX-remote" mv /extra/parked '/extra/film/JM Sonda Mobile (2026)'
scan remote
sync_now local; settle local; scan local
check "the merge applies again" true "$(list local | jq --arg i "$(merge_id local '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "both stubs again, as the items they were" "2 $mob_a" "$(strm_count 'JM Sonda Mobile') $(film_of remote "JM Sonda Mobile")"
check "linked again" "$mob_b:$mob_a" "$(group "$mob_a")"

echo "--- Q145: a new title on both servers is linked by the scan that makes its items"
add_clip remote "$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv" "JM Nuovo (2026)" "JM Nuovo" 999100031 "" "Nuovo."
add_clip peer "$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv" "JM Nuovo (2026)" "JM Nuovo" 999100031 "" "Nuovo."
scan remote; scan peer
sync_now local
check "the sync wrote both stubs" 2 "$(strm_count 'JM Nuovo')"
settle local
scan local
new_a=$(film_of remote "JM Nuovo"); new_b=$(film_of peer "JM Nuovo")
check "the scan made the items and linked them, with no sync after it" "$new_b:$new_a" "$(group "$new_a")"

echo "--- undoing the chain"
check "the undo is accepted" 204 "$(undo local '^JM Catena')"
applied local
tre2=$(film_of remote "JM Catena Tre")
check "three stubs again" 3 "$(strm_count 'JM Catena')"
check "the survivor is the same item" "$uno" "$(film_of remote "JM Catena Uno")"
check "and keeps all the watch state" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$uno")"
# Jellyfin's doing: the stub is back on its old path, so it is the same item and the rows left under the placeholder return.
check "the stub of Tre is back as the same item, with its state" "$tre {\"Played\":true,\"PlayCount\":1,\"Favorite\":true}" "$tre2 $(state "$tre2")"
check "the chain is listed again" 3 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena"))] | length')"
check "the group is dissolved at once (D5)" ":" "$(primary_of "$uno")$(primary_of "$due"):$(alternates_of "$uno")$(alternates_of "$due")"
sync_now local
check "and stays dissolved after a sync" ":" "$(primary_of "$uno")$(primary_of "$due"):$(alternates_of "$uno")$(alternates_of "$due")"

echo "--- the configuration page"
sid=$(curl -fsS "http://127.0.0.1:$PORT/System/Info/Public" | jq -r .Id)
mkdir -p "$WORK/web"
cp "$(dirname "${BASH_SOURCE[0]}")/configpage.py" "$WORK/web/configpage.py"
docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v "$PREFIX-pw:/pw" -e PYTHONPATH=/pw "$PW_IMAGE" \
  bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
  /w/configpage.py "http://host.docker.internal:$PORT" "$(token_of local)" "$uid" "$sid" /w/page | tee "$WORK/web/page.out"
check "the page lists the chain" true "$(grep '^CASE ' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("JM Catena Tre") and contains("remote") and contains("peer") and contains("min")')"
check "the page does not offer a merge of one title, and says why" true "$(grep '^REFUSED' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'startswith("disabled") and contains("at least two")')"
check "the page says what would play" true "$(grep '^FORECAST' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("on peer plays")')"
check "the page merged" true "$(grep '^MERGED' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("Merged")')"
check "and lists the merge" true "$(grep '^MERGE ' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("JM Catena Due")')"
check "the page undid it" true "$(grep '^UNDONE' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("undone")')"
applied local
check "the page left three stubs" 3 "$(strm_count 'JM Catena')"
check "the survivor still has its state" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$uno")"

echo "--- a local copy: the stub stays, the local copy is the primary and takes the state but the position (Q18 b, Q43, D4, decision 172)"
# A copy on remote and a local one, with no IDs and plots that disagree: a case for the admin, not a match.
add_clip remote "$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv" "JM Sonda Locale (2026)" "JM Sonda Locale" "" "" "Un pianista insegue un sogno tra le colline."
scan remote
LOCAL_DIR="/media/film/JM Sonda Locale (2026)"
docker exec "$PREFIX-local" mkdir -p "$LOCAL_DIR"
docker cp "$MEDIA/locale/film/Sonda Locale Senza ID (2026)/Sonda Locale Senza ID (2026).mkv" "$PREFIX-local:$LOCAL_DIR/JM Sonda Locale (2026).mkv" >/dev/null
printf '%s' '<?xml version="1.0" encoding="utf-8"?><movie><title>JM Sonda Locale</title><year>2026</year><plot>Una spedizione polare perde i contatti con la base.</plot></movie>' |
  docker exec -i "$PREFIX-local" sh -c "cat >'$LOCAL_DIR/movie.nfo'"
docker exec "$PREFIX-local" chmod -R a+rwX "$LOCAL_DIR"
scan local
sync_now local
settle local
scan local
strm=$(film_of remote "JM Sonda Locale")
local_id=$(vid "$LOCAL_DIR/")
check "a remote copy of a local film is listed" true "$(list local | jq '[.Cases[].Titles[] | select(.HasLocalCopy) | .Copies[].Copy.Title] | any(. == "JM Sonda Locale")')"
check "the stub and the local copy are apart" "1 :" "$([ -n "$strm" ] && [ -n "$local_id" ] && echo 1 || echo 0) $(primary_of "$strm"):$(primary_of "$local_id")"
sum=$(docker exec "$PREFIX-local" sh -c "cd '$LOCAL_DIR' && ls -la --time-style=full-iso . && sha256sum *")
post local "/UserPlayedItems/$(dashed "$strm")?userId=$uid" >/dev/null
post local "/UserItems/$(dashed "$strm")/UserData?userId=$uid" '{"PlaybackPositionTicks": 300000000}' >/dev/null
post local "/UserFavoriteItems/$(dashed "$strm")?userId=$uid" >/dev/null
check "the local copy starts unplayed" '{"Played":false,"PlayCount":0,"Favorite":false}' "$(state "$local_id")"
check "the merge is accepted" 204 "$(merge local '^JM Sonda Locale$')"
check "linked at once, the local copy as primary" "$local_id:$strm" "$(group "$strm")"
applied local
check "the decision is local and manual" 1 "$(get local /JellyMesh/Admin/Index/Decisions | jq '[.Contents[] | select(.Identity.Match == "Manual" and .IsLocal)] | length')"
check "the stub stays" 1 "$(strm_count 'JM Sonda Locale')"
check "one row for the client" 1 "$(rows 'JM Sonda Locale')"
check "played, play count and favorite go to the local copy" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$local_id")"
check "the stub keeps its position" 300000000 "$(get local "/Users/$uid/Items/$(dashed "$strm")" | jq .UserData.PlaybackPositionTicks)"
check "and the local copy takes none" 0 "$(get local "/Users/$uid/Items/$(dashed "$local_id")" | jq .UserData.PlaybackPositionTicks)"
check "the local files did not change" "$sum" "$(docker exec "$PREFIX-local" sh -c "cd '$LOCAL_DIR' && ls -la --time-style=full-iso . && sha256sum *")"
check "the undo is accepted" 204 "$(undo local '^JM Sonda Locale$')"
applied local
check "the stub stays, as the same item" "$strm" "$(film_of remote "JM Sonda Locale")"
check "the local copy keeps played" true "$(state "$local_id" | jq .Played)"
check "the group is dissolved" ":" "$(primary_of "$strm"):$(alternates_of "$local_id")"
check "the local files did not change" "$sum" "$(docker exec "$PREFIX-local" sh -c "cd '$LOCAL_DIR' && ls -la --time-style=full-iso . && sha256sum *")"

exit "$FAILED"
