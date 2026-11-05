#!/usr/bin/env bash
# Decision 20 on a real server: list, merge, restart, sync, a copy that changes or goes, undo, and a local copy. Needs up.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

sync_and_scan fresh
uid=$(admin_id fresh)
FEDERATED=/federata/Film

echo "--- the list"
list fresh | jq -c '.Cases[] | {Kind, Titles: [.Titles[] | [.Copies[] | "\(.Copy.Title) \(.Copy.Year) \(.ServerName // "local") \(.RunTimeTicks)"]]}'
check "the chain is one case of three titles" 3 "$(list fresh | jq '[.Cases[] | select(.Kind == "ConflictingProviderIds") | select(any(.Titles[].Copies[]; .Copy.Title == "JM Catena Uno")) | .Titles[]] | length')"
check "copies say where they live" '["peer","stock","stock"]' "$(list fresh | jq -c '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena")) | .ServerName] | sort')"
check "copies have a runtime" true "$(list fresh | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena")) | .RunTimeTicks > 0] | all')"
check "two local films are not listed" 0 "$(list fresh | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title == "JM Due Locali")] | length')"
check "but they stay reported" true "$(get fresh /JellyMesh/Admin/Index/Decisions | jq '[.Issues[] | select(.Kind == "PossibleDuplicate") | .WorkKeys | length > 1] | any')"
check "a remote copy of a local film is listed" true "$(list fresh | jq '[.Cases[].Titles[] | select(.HasLocalCopy) | .Copies[].Copy.Title] | any(. == "Sonda Trama")')"
check "the list holds no address" 0 "$(list fresh | grep -c 'jm-merge-' || true)"

curl -sS -o "$WORK/peer-user" -H "$ACCEPT" -H "Authorization: $AUTH" -H 'Content-Type: application/json' \
  -d '{"Username":"jellymesh","Pw":"jellymesh-test"}' "http://127.0.0.1:18441/Users/AuthenticateByName"
user_token=$(jq -r .AccessToken "$WORK/peer-user")
check "a user without admin rights gets no list" 403 "$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$user_token\"" http://127.0.0.1:18441/JellyMesh/Admin/Merges)"
check "nor a merge" 403 "$(curl -sS -o /dev/null -w '%{http_code}' -X POST -H "Authorization: $AUTH, Token=\"$user_token\"" -H 'Content-Type: application/json' -d '{"WorkKeys":["a","b"]}' http://127.0.0.1:18441/JellyMesh/Admin/Merges)"

echo "--- merging the chain"
uno=$(film_by_path fresh "JM Catena Uno (2026)")
due=$(film_by_path fresh "JM Catena Due (2026)")
tre=$(film_by_path fresh "JM Catena Tre (2026)")
post fresh "/UserPlayedItems/$due?userId=$uid" >/dev/null
post fresh "/UserFavoriteItems/$tre?userId=$uid" >/dev/null
before=$(stubs fresh | wc -l | tr -d ' ')

check "one title cannot be merged" 400 "$(post fresh /JellyMesh/Admin/Merges '{"WorkKeys":["only one"]}' >/dev/null; cat "$WORK/status")"
check "the merge is accepted" 204 "$(merge fresh '^JM Catena')"
applied fresh

check "one stub left of three" "$((before - 2))" "$(stubs fresh | wc -l | tr -d ' ')"
check "the stub of the lowest key stayed" 1 "$(strm_count fresh 'JM Catena Uno (2026).strm')"
check "the same item" "$uno" "$(film_by_path fresh "JM Catena Uno (2026)")"
check "one title for the client, under the name of the winner" "0 1 0" "$(film_rows fresh 'JM Catena Uno') $(film_rows fresh 'JM Catena Due') $(film_rows fresh 'JM Catena Tre')"
check "the best quality wins, the others are backups" '{"Winner":"peer","Height":1080,"Backups":["stock","stock"],"Match":"Manual"}' \
  "$(get fresh /JellyMesh/Admin/Index/Decisions | jq -c '[.Contents[] | select(.Identity.Key | contains("999100001")) | {Winner: .Winner.ServerName, Height: .Winner.Quality.Height, Backups: [.Backups[].ServerName], Match: .Identity.Match}] | first')"
check "the case is gone" 0 "$(list fresh | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena"))] | length')"
check "the stub has all the watch state, though its IDs changed" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$uno")"

info=$(post fresh "/Items/$uno/PlaybackInfo?userId=$uid" "$DIRECT")
check "PlaybackInfo answers" 200 "$(cat "$WORK/status")"
source_id=$(echo "$info" | jq -r '.MediaSources[0].Id')
check "the winner plays" 206 "$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(token_of fresh)\"" -H "Range: bytes=0-1023" "http://127.0.0.1:18441/Videos/$uno/stream?static=true&mediaSourceId=$source_id")"
check "no remote address in PlaybackInfo" 0 "$(echo "$info" | grep -c 'jm-merge-' || true)"

echo "--- merging the pair without IDs"
check "the pair is listed" 2 "$(list fresh | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title == "JM Sonda Mobile")] | length')"
check "the merge is accepted" 204 "$(merge fresh '^JM Sonda Mobile$')"
applied fresh
check "one title" 1 "$(film_rows fresh 'JM Sonda Mobile')"
check "one stub" 1 "$(strm_count fresh 'JM Sonda Mobile')"

echo "--- a restart and a full sync"
docker exec jm-merge-f sh -c 'find /config -name manual-merges.json' | sed 's/^/stored in /'
docker restart jm-merge-f >/dev/null
sleep 3
wait_ready fresh
login fresh
check "the merges are back" 2 "$(list fresh | jq '.Merges | length')"
check "and in effect" true "$(list fresh | jq '[.Merges[].IsInEffect] | all')"
sync_and_scan fresh
sync_and_scan fresh
check "still one title" 1 "$(film_rows fresh 'JM Catena Due')"
check "no stub came back" 0 "$(strm_count fresh 'JM Catena Due (2026).strm')"
check "the watch state stayed" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$uno")"

echo "--- a copy gains IDs"
docker exec jm-merge-j sh -c "sed -i 's#</movie>#<uniqueid type=\"tmdb\" default=\"true\">999100021</uniqueid></movie>#' '/extra/film/JM Sonda Mobile (2026)/movie.nfo'"
remote=$(items peer Movie | jq -r '.Items[] | select(.Path | contains("JM Sonda Mobile")) | .Id')
post peer "/Items/$remote/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=false&replaceAllImages=false" >/dev/null
sleep 5
check "the remote has the ID" 999100021 "$(get peer "/Users/$(admin_id peer)/Items/$remote" | jq -r .ProviderIds.Tmdb)"
sync_and_scan fresh
check "the merge holds" true "$(list fresh | jq --arg i "$(merge_id fresh '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "still one title" 1 "$(film_rows fresh 'JM Sonda Mobile')"
check "still one stub" 1 "$(strm_count fresh 'JM Sonda Mobile')"

echo "--- a copy loses its year: \"Replace all metadata\" on a remote does that"
post peer "/Items/$remote/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false" >/dev/null
sleep 5
check "the remote has no year" null "$(get peer "/Users/$(admin_id peer)/Items/$remote" | jq -r .ProductionYear)"
sync_and_scan fresh
check "the merge holds" true "$(list fresh | jq --arg i "$(merge_id fresh '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "still one stub" 1 "$(strm_count fresh 'JM Sonda Mobile')"

echo "--- a copy disappears, and comes back"
docker exec jm-merge-s mv '/extra/film/JM Sonda Mobile (2026)' /extra/parked
scan stock
sync_and_scan fresh
check "the merge waits" false "$(list fresh | jq --arg i "$(merge_id fresh '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "for the copy that is missing" 1 "$(list fresh | jq --arg i "$(merge_id fresh '^JM Sonda Mobile$')" '[.Merges[] | select(.Id == $i) | .Copies[] | select(.IsFound | not)] | length')"
check "the title stays" 1 "$(film_rows fresh 'JM Sonda Mobile')"
docker exec jm-merge-s mv /extra/parked '/extra/film/JM Sonda Mobile (2026)'
scan stock
sync_and_scan fresh
check "the merge applies again" true "$(list fresh | jq --arg i "$(merge_id fresh '^JM Sonda Mobile$')" '.Merges[] | select(.Id == $i) | .IsInEffect')"
check "still one title" 1 "$(film_rows fresh 'JM Sonda Mobile')"
check "still one stub" 1 "$(strm_count fresh 'JM Sonda Mobile')"

echo "--- undoing the chain"
check "the undo is accepted" 204 "$(undo fresh '^JM Catena')"
applied fresh
sync_and_scan fresh
check "three stubs again" 3 "$(strm_count fresh 'JM Catena')"
check "three titles for the client" "1 1 1" "$(film_rows fresh 'JM Catena Uno') $(film_rows fresh 'JM Catena Due') $(film_rows fresh 'JM Catena Tre')"
check "the survivor is the same item, under its own name again" "$uno" "$(film_by_path fresh "JM Catena Uno (2026)")"
check "the survivor keeps all the watch state" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$uno")"
due2=$(film_by_path fresh "JM Catena Due (2026)")
tre2=$(film_by_path fresh "JM Catena Tre (2026)")
echo "separated copies: Due $due -> $due2 $(state fresh "$due2"), Tre $tre -> $tre2 $(state fresh "$tre2")"
# Jellyfin's doing: the stub is back on its old path, so it is the same item and the rows left under the placeholder return.
check "the separated copies keep their state from before the merge" '{"Played":true,"PlayCount":1,"Favorite":false} {"Played":false,"PlayCount":0,"Favorite":true}' "$(state fresh "$due2") $(state fresh "$tre2")"
check "the chain is listed again" 3 "$(list fresh | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | startswith("JM Catena"))] | length')"

echo "--- undoing two copies with one folder name and no IDs: the second stub needs a new path, so nothing brings its state back"
gemella_a=$(film_by_path fresh "JM Sonda Gemella (2026)/")
gemella_b=$(film_by_path fresh "JM Sonda Gemella (2026) [")
post fresh "/UserPlayedItems/$gemella_a?userId=$uid" >/dev/null
post fresh "/UserFavoriteItems/$gemella_b?userId=$uid" >/dev/null
check "the merge is accepted" 204 "$(merge fresh '^JM Sonda Gemella$')"
applied fresh
check "one stub" 1 "$(strm_count fresh 'JM Sonda Gemella')"
check "the survivor has both" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$gemella_a")"
check "the undo is accepted" 204 "$(undo fresh '^JM Sonda Gemella$')"
applied fresh
sync_and_scan fresh
check "two stubs" 2 "$(strm_count fresh 'JM Sonda Gemella')"
check "the survivor keeps both" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$gemella_a")"
gemella_new=$(film_by_path fresh "JM Sonda Gemella (2026) [")
check "the other stub is a new item" true "$([ "$gemella_new" != "$gemella_b" ] && echo true || echo false)"
check "and starts with no state" '{"Played":false,"PlayCount":0,"Favorite":false}' "$(state fresh "$gemella_new")"

echo "--- the configuration page"
sid=$(curl -fsS http://127.0.0.1:18441/System/Info/Public | jq -r .Id)
mkdir -p "$WORK/web"
cp "$(dirname "${BASH_SOURCE[0]}")/configpage.py" "$WORK/web/configpage.py"
docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v jm-merge-pw:/pw -e PYTHONPATH=/pw "$PW_IMAGE" \
  bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
  /w/configpage.py http://host.docker.internal:18441 "$(token_of fresh)" "$uid" "$sid" /w/page | tee "$WORK/web/page.out"
check "the page lists the chain" true "$(grep '^CASE ' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("JM Catena Tre") and contains("stock") and contains("peer") and contains("min")')"
check "the page shows a refusal" true "$(grep '^REFUSED' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("at least two")')"
check "the page merged" true "$(grep '^MERGED' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("Merged")')"
check "and lists the merge" true "$(grep '^MERGE ' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("JM Catena Due")')"
check "the page undid it" true "$(grep '^UNDONE' "$WORK/web/page.out" | cut -d' ' -f2- | jq 'contains("undone")')"
applied fresh
check "the page left three stubs" 3 "$(strm_count fresh 'JM Catena')"
check "the survivor still has its state" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state fresh "$uno")"

echo "--- a local copy"
strm=$(film_by_path fresh "Sonda Trama (2026).strm")
local_id=$(items fresh Movie | jq -r '.Items[] | select(.Name == "Sonda Trama") | select(.Path | startswith("/media")) | .Id')
sum=$(docker exec jm-merge-f sh -c 'cd "/media/film/Sonda Trama (2026)" && ls -la --time-style=full-iso . && sha256sum *')
post fresh "/UserPlayedItems/$strm?userId=$uid" >/dev/null
check "the merge is accepted" 204 "$(merge fresh '^Sonda Trama$')"
applied fresh
check "the local copy wins" true "$(get fresh /JellyMesh/Admin/Index/Decisions | jq --arg t 'sonda trama' '[.Contents[] | select(.Identity.Key | contains($t))] | first | .IsLocal')"
check "the stub is gone" 0 "$(strm_count fresh 'Sonda Trama')"
check "one title for the client" 1 "$(film_rows fresh 'Sonda Trama')"
check "the local copy has the watch state" true "$(state fresh "$local_id" | jq .Played)"
check "the local files did not change" "$sum" "$(docker exec jm-merge-f sh -c 'cd "/media/film/Sonda Trama (2026)" && ls -la --time-style=full-iso . && sha256sum *')"

check "the undo is accepted" 204 "$(undo fresh '^Sonda Trama$')"
applied fresh
check "the stub is back" 1 "$(strm_count fresh 'Sonda Trama')"
check "two titles for the client" 2 "$(film_rows fresh 'Sonda Trama')"
check "the local copy keeps the watch state" true "$(state fresh "$local_id" | jq .Played)"
strm2=$(film_by_path fresh "Sonda Trama (2026).strm")
echo "separated stub: $strm -> $strm2 $(state fresh "$strm2")"
check "the local files did not change" "$sum" "$(docker exec jm-merge-f sh -c 'cd "/media/film/Sonda Trama (2026)" && ls -la --time-style=full-iso . && sha256sum *')"

exit "${FAILED:-0}"
