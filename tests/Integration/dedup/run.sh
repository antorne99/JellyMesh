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

echo "--- decision 13a: stubs"
stubs fresh | grep "JM Serie Doppia " | sed 's/.*Season 01.//'
check "stub names" "JM Serie Doppia S01E03-E04.strm,JM Serie Doppia S01E05-E06.strm,JM Serie Doppia S01E08.strm,JM Serie Doppia S01E09.strm,JM Serie Doppia S01E10.strm" \
  "$(stubs fresh | grep "JM Serie Doppia " | sed 's/.*Season 01.//' | sort | paste -sd, -)"
docker exec jm-dedup-f sh -c 'cat "/federata/Serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E03-E04.nfo"' >"$WORK/double.nfo"
check "the nfo holds the first number" 1 "$(grep -c '<episode>3</episode>' "$WORK/double.nfo")"
check "the nfo holds the end" 1 "$(grep -c '<episodenumberend>4</episodenumberend>' "$WORK/double.nfo")"
check "the nfo is locked" 1 "$(grep -c '<lockdata>true</lockdata>' "$WORK/double.nfo")"
check "the nfo holds no address" 0 "$(grep -c -E 'jm-dedup|http' "$WORK/double.nfo" || true)"
check "the merged series as a client sees it" '["E1-2@local","E3-4@stub","E5-6@stub","E7@local","E8@stub","E9@stub","E10@stub"]' "$(doppia fresh)"

echo "--- decision 13a: playback of a stub of a double"
id=$(episode_id fresh "S01E03-E04.strm")
info=$(post fresh "/Items/$id/PlaybackInfo?userId=$(admin_id fresh)" "$DIRECT")
check "PlaybackInfo answers" 200 "$(cat "$WORK/status")"
source_id=$(echo "$info" | jq -r '.MediaSources[0].Id')
code=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(token_of fresh)\"" -H "Range: bytes=0-1023" "http://127.0.0.1:18421/Videos/$id/stream?static=true&mediaSourceId=$source_id")
check "the double streams from the remote" 206 "$code"
check "no remote address in PlaybackInfo" 0 "$(echo "$info" | grep -c 'jm-dedup-' || true)"

echo "--- NextUp walks past a stub of a double"
uid=$(admin_id fresh)
series=$(items fresh Episode | jq -r '[.Items[] | select(.SeriesName == "JM Serie Doppia")][0].SeriesId')
post fresh "/UserPlayedItems/$(episode_id fresh "S01E01-E02.mkv")?userId=$uid" >/dev/null
check "after the local double" "3" "$(get fresh "/Shows/NextUp?userId=$uid&seriesId=$series" | jq -r '.Items[0].IndexNumber')"
post fresh "/UserPlayedItems/$id?userId=$uid" >/dev/null
check "after the stub of the double" "5" "$(get fresh "/Shows/NextUp?userId=$uid&seriesId=$series" | jq -r '.Items[0].IndexNumber')"

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

echo "--- local copies arrive"
docker exec jm-dedup-f sh -c 'mkdir -p "/extra/serie/JM Serie Doppia (2026)/Season 01"'
docker cp "$MEDIA/locale/serie/JM Serie Doppia (2026)/tvshow.nfo" "jm-dedup-f:/extra/serie/JM Serie Doppia (2026)/tvshow.nfo"
docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "jm-dedup-f:/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E03.mkv"
scan fresh
settle fresh
sync_and_scan fresh
check "E3 local: the stub of E03-E04 shrinks to E4 and stays" '["E1-2@local","E3@local","E4@stub","E5-6@stub","E7@local","E8@stub","E9@stub","E10@stub"]' "$(doppia fresh)"
check "the stub kept its path" 1 "$(stubs fresh | grep -c 'S01E03-E04.strm')"
check "the stub kept its state" true "$(get fresh "/Users/$uid/Items/$id" | jq .UserData.Played)"

docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "jm-dedup-f:/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E04.mkv"
scan fresh
settle fresh
check "E4 local too: the stub goes" 0 "$(stubs fresh | grep -c 'S01E03-E04.strm' || true)"
check "its state reached the local episode" true "$(get fresh "/Users/$uid/Items/$(episode_id fresh "JM Serie Doppia S01E04.mkv")" | jq .UserData.Played)"

s56=$(episode_id fresh "S01E05-E06.strm")
post fresh "/UserPlayedItems/$s56?userId=$uid" >/dev/null
docker cp "$MEDIA/remoto-standard/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv" "jm-dedup-f:/extra/serie/JM Serie Doppia (2026)/Season 01/"
scan fresh
settle fresh
check "a local double replaces the stub of a double" '["E1-2@local","E3@local","E4@local","E5-6@local","E7@local","E8@stub","E9@stub","E10@stub"]' "$(doppia fresh)"
check "its state reached the local double" true "$(get fresh "/Users/$uid/Items/$(episode_id fresh "/extra/serie/JM Serie Doppia (2026)/Season 01/JM Serie Doppia S01E05-E06.mkv")" | jq .UserData.Played)"

exit "${FAILED:-0}"
