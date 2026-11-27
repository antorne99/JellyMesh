#!/usr/bin/env bash
# Decisions 13 and 13a on a consumer that starts from an empty federated folder. Needs up.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

W="series|tmdb:999000201"
sync_and_scan fresh

echo "--- decision 13"
check "a copy without IDs joins the copy with IDs" '{"local":false,"winner":"peer","height":1080,"backups":["stock"]}' "$(content fresh 'movie|tmdb:999000008')"
check "one shared ID merges" '{"local":false,"winner":"peer","height":1080,"backups":["stock"]}' "$(content fresh 'movie|tmdb:999000009')"
check "one film shown for the copy without IDs" 1 "$(film_rows fresh 'JM Copia Senza ID')"
check "one film shown for partial IDs" 1 "$(film_rows fresh 'JM ID Parziali')"
check "same runtime, plots differ: first title stays" 1 "$(film_rows fresh 'La La Sonda Muta')"
check "same runtime, plots differ: second title stays" 1 "$(film_rows fresh 'Sonda Muta')"
check "the doubt is reported" true "$(get fresh /JellyMesh/Admin/Index/Decisions | jq '[.Issues[] | select(.Kind == "PossibleDuplicate") | .WorkKeys[] | contains("sonda muta")] | any')"
check "a merge on differing IDs is reported" true "$(get fresh /JellyMesh/Admin/Index/Decisions | jq '[.Issues[] | select(.Kind == "ConflictingProviderIds") | .WorkKeys[] | contains("999000009")] | any')"
check "the runtime confirms a pair without IDs" '{"local":false,"winner":"peer","height":1080,"backups":["stock"]}' "$(content fresh 'movie|title:sonda senza id|2026')"
check "the runtime lets the local copy win" true "$(content fresh 'movie|title:sonda locale senza id|2026' | jq .local)"
check "the plot of a local copy counts: it hides nothing" 2 "$(film_rows fresh 'Sonda Trama')"
check "a different year stays apart" 2 "$(items fresh Movie | jq '[.Items[] | select(.Name | startswith("Titolo Omonimo"))] | length')"

echo "--- decision 13a: what the index holds, per episode"
check "a JellyMesh peer sends the end of a double too" 2 "$(get fresh /JellyMesh/Admin/Index/Decisions | jq --arg k "$W|S1E6" '[.Contents[] | select(.Identity.Key == $k) | .RemoteItems[]] | length')"
for ep in 1 2; do check "E$ep: the local double covers it" true "$(content fresh "$W|S1E$ep" | jq .local)"; done
for ep in 3 4; do check "E$ep: the remote double is the source" '"stock"' "$(content fresh "$W|S1E$ep" | jq .winner)"; done
for ep in 5 6; do check "E$ep: best double, the other as backup" '{"local":false,"winner":"peer","height":1080,"backups":["stock"]}' "$(content fresh "$W|S1E$ep")"; done
check "E7: local wins over the double" true "$(content fresh "$W|S1E7" | jq .local)"
check "E8: the double still provides it" '"stock"' "$(content fresh "$W|S1E8" | jq .winner)"
check "E9: from the double" '"stock"' "$(content fresh "$W|S1E9" | jq .winner)"
check "E10: the better single wins, the double is its backup" '{"local":false,"winner":"peer","height":1080,"backups":["stock"]}' "$(content fresh "$W|S1E10")"


echo "--- decision 13a: stubs, one per copy of each server with its label (rows 61, 62, 64)"
stock=$(cat "$WORK/id-fresh-stock"); peer=$(cat "$WORK/id-fresh-peer")
folder_of() { local id=${1//-/}; echo "${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"; }
doubles() { stubs fresh | grep "JM Serie Doppia " | grep -F "/$(folder_of "$1")/" | sed 's/.*Season 01.//' | sort | paste -sd, -; }
check "stubs of stock, the local episodes included" \
  "JM Serie Doppia S01E01 - stock.strm,JM Serie Doppia S01E02 - stock.strm,JM Serie Doppia S01E03-E04 - stock.strm,JM Serie Doppia S01E05-E06 - stock.strm,JM Serie Doppia S01E07-E08 - stock.strm,JM Serie Doppia S01E09-E10 - stock.strm" \
  "$(doubles "$stock")"
check "stubs of peer" "JM Serie Doppia S01E05-E06 - peer.strm,JM Serie Doppia S01E10 - peer.strm" "$(doubles "$peer")"
nfo=$(stubs fresh | grep -F "/$(folder_of "$stock")/" | grep 'JM Serie Doppia S01E03-E04 - stock.strm' | sed 's/\.strm$/.nfo/')
docker exec "$PREFIX-f" cat "/federata/${nfo#./}" >"$WORK/double.nfo"
check "the nfo holds the first number" 1 "$(grep -c '<episode>3</episode>' "$WORK/double.nfo")"
check "the nfo holds the end" 1 "$(grep -c '<episodenumberend>4</episodenumberend>' "$WORK/double.nfo")"
check "the nfo is locked" 1 "$(grep -c '<lockdata>true</lockdata>' "$WORK/double.nfo")"
check "the nfo holds no address" 0 "$(grep -c -E "$PREFIX|http" "$WORK/double.nfo" || true)"
check "the merged series as a client sees it" '["E1@stock","E1-2@local","E2@stock","E3-4@stock","E5-6@peer","E7@local","E7-8@stock","E9-10@stock","E10@peer"]' "$(doppia fresh)"
s56=$(episode_id fresh "S01E05-E06 - peer.strm")
check "the doubles E05-E06 of both servers are versions of one episode, the better one primary" '["peer","stock"]' "$(versions fresh "$s56")"

echo "--- decision 13a: playback of a stub of a double"
id=$(episode_id fresh "S01E03-E04 - stock.strm")
info=$(post fresh "/Items/$id/PlaybackInfo?userId=$(admin_id fresh)" "$DIRECT")
check "PlaybackInfo answers" 200 "$(cat "$WORK/status")"
source_id=$(echo "$info" | jq -r '.MediaSources[0].Id')
code=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(token_of fresh)\"" -H "Range: bytes=0-1023" "http://127.0.0.1:$PORT/Videos/$id/stream?static=true&mediaSourceId=$source_id")
check "the double streams from the remote" 206 "$code"
check "no remote address in PlaybackInfo" 0 "$(echo "$info" | grep -c "$PREFIX-" || true)"

echo "--- NextUp walks past a stub of a double"
uid=$(admin_id fresh)
series=$(items fresh Episode | jq -r '[.Items[] | select(.SeriesName == "JM Serie Doppia")][0].SeriesId')
next_up() { get fresh "/Shows/NextUp?userId=$uid&seriesId=$series&Fields=Path" | jq -r '.Items[0] | "E\(.IndexNumber)@\(.Path | split(" - ") | last | rtrimstr(".strm"))"'; }
post fresh "/UserPlayedItems/$(episode_id fresh "S01E01-E02.mkv")?userId=$uid" >/dev/null
echo "observed, not judged: after the local double alone NextUp offers $(next_up), the single E02 of stock"
for f in "S01E01 - stock.strm" "S01E02 - stock.strm"; do post fresh "/UserPlayedItems/$(episode_id fresh "$f")?userId=$uid" >/dev/null; done
check "after E01 and E02 of every copy" "E3@stock" "$(next_up)"
post fresh "/UserPlayedItems/$id?userId=$uid" >/dev/null
check "after the stub of the double" 5 "$(next_up | cut -d@ -f1 | tr -d E)"
echo "observed, not judged: NextUp names the version $(next_up | cut -d@ -f2) of E05-E06, whose primary is peer"

echo "--- a refresh keeps the range"
post fresh "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false" >/dev/null
sleep 5
check "locked stub after replace all" '{"E":3,"End":4}' "$(get fresh "/Users/$uid/Items/$id" | jq -c '{E:.IndexNumber,End:.IndexNumberEnd}')"
configure fresh false
sync_and_scan fresh
check "the range survives unlocking" '{"E":3,"End":4,"Lock":false}' "$(get fresh "/Users/$uid/Items/$id" | jq -c '{E:.IndexNumber,End:.IndexNumberEnd,Lock:.LockData}')"
post fresh "/Items/$id/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=true&replaceAllImages=false" >/dev/null
sleep 5
check "unlocked stub after replace all" '{"E":3,"End":4}' "$(get fresh "/Users/$uid/Items/$id" | jq -c '{E:.IndexNumber,End:.IndexNumberEnd}')"
configure fresh true
sync_and_scan fresh

echo "--- local copies arrive: the stubs stay (row 64), a local copy of the same range becomes the primary"
local_state() { get fresh "/Users/$uid/Items/$(episode_id fresh "/extra/serie/JM Serie Doppia (2026)/Season 01/$1")" | jq -c .UserData.Played; }
docker exec "$PREFIX-f" sh -c 'mkdir -p "/extra/serie/JM Serie Doppia (2026)/Season 01"'
docker cp "$MEDIA/locale/serie/JM Serie Doppia (2026)/tvshow.nfo" "$PREFIX-f:/extra/serie/JM Serie Doppia (2026)/tvshow.nfo"
docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "$PREFIX-f:/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E03.mkv"
scan fresh
settle fresh
sync_and_scan fresh
check "E3 local: the stub of E03-E04 stays" 1 "$(stubs fresh | grep -c 'S01E03-E04 - stock.strm')"
check "the stub is the same item and keeps its state" "$id true" "$(episode_id fresh 'S01E03-E04 - stock.strm') $(get fresh "/Users/$uid/Items/$id" | jq .UserData.Played)"
echo "observed, not judged: the series is $(doppia fresh)"

docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "$PREFIX-f:/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E04.mkv"
scan fresh
settle fresh
sync_and_scan fresh
check "E4 local too: the stub of E03-E04 still stays" 1 "$(stubs fresh | grep -c 'S01E03-E04 - stock.strm')"
check "and keeps its state" true "$(get fresh "/Users/$uid/Items/$id" | jq .UserData.Played)"
echo "observed, not judged: with the whole range local, played on local E03 and E04: $(local_state 'JM Serie Doppia S01E03.mkv') $(local_state 'JM Serie Doppia S01E04.mkv'); the series is $(doppia fresh)"

post fresh "/UserPlayedItems/$s56?userId=$uid" >/dev/null
docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "$PREFIX-f:/extra/serie/JM Serie Doppia (2026)/Season 01/"
scan fresh
settle fresh
sync_and_scan fresh
check "a local double joins the stubs of the same range as their primary" \
  '["E1@stock","E1-2@local","E2@stock","E3@local","E3-4@stock","E4@local","E5-6@local","E7@local","E7-8@stock","E9-10@stock","E10@peer"]' "$(doppia fresh)"
check "both stubs of E05-E06 stay" 2 "$(stubs fresh | grep -c 'S01E05-E06 - ')"
local56=$(episode_id fresh "/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv")
check "the local double has the stubs as versions" '["JM Serie Doppia S01E05-E06","peer","stock"]' "$(versions fresh "$local56")"
check "the state of the stubs reached the local primary" true "$(local_state 'JM Serie Doppia S01E05-E06.mkv')"
post fresh "/UserPlayedItems/$(episode_id fresh "/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E04.mkv")?userId=$uid" >/dev/null
for i in "$local56" "$s56" "$(episode_id fresh "S01E05-E06 - stock.strm")"; do req fresh DELETE "/UserPlayedItems/$i?userId=$uid" >/dev/null; done
echo "observed, not judged: with E05-E06 unplayed, NextUp names $(get fresh "/Shows/NextUp?userId=$uid&seriesId=$series&Fields=Path" | jq -r '.Items[0].Path | split("/") | last')"

exit "${FAILED:-0}"
