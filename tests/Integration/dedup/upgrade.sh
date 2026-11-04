#!/usr/bin/env bash
# An index and stubs written by the build before decisions 13 and 13a, then the current build on the same data.
# usage: OLD=<checkout of the old build> [MEDIA=...] [WORK=...] upgrade.sh. Needs up.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
OLD=${OLD:?point OLD at a checkout of the build to upgrade from}

start upgrade
install_plugin upgrade "$OLD"
configure upgrade true
add_remote upgrade stock http://jm-dedup-s:8096 >/dev/null
add_remote upgrade peer http://jm-dedup-j:8096 >/dev/null
sync_and_scan upgrade

echo "--- before"
stubs upgrade | grep -E "ID Parziali|Sonda Muta|Serie Doppia " | sed 's/.*\///'
uid=$(admin_id upgrade)
check "the old build shows partial IDs twice" 2 "$(film_rows upgrade 'JM ID Parziali')"
check "the old build merged title and year alone" 1 "$(( $(film_rows upgrade 'La La Sonda Muta') + $(film_rows upgrade 'Sonda Muta') ))"
check "the old build has a plain stub of a double" 1 "$(stubs upgrade | grep -c 'S01E03.strm')"
items upgrade Movie | jq -c '[.Items[] | select(.Name == "JM ID Parziali") | .Path]'
# One of the two stubs is played and the other a favorite: whichever stays, it has to end up with both.
post upgrade "/UserPlayedItems/$(items upgrade Movie | jq -r '[.Items[] | select(.Name == "JM ID Parziali") | .Id] | first')?userId=$uid" >/dev/null
post upgrade "/UserFavoriteItems/$(items upgrade Movie | jq -r '[.Items[] | select(.Name == "JM ID Parziali") | .Id] | last')?userId=$uid" >/dev/null
stub3=$(episode_id upgrade "S01E03.strm")
post upgrade "/UserPlayedItems/$stub3?userId=$uid" >/dev/null
stubs_before=$(stubs upgrade | wc -l | tr -d ' ')

echo "--- the current build on the same data, before any sync"
install_plugin upgrade
health=$(get upgrade /JellyMesh/Admin/Index/Decisions | jq -c '{Health, Contents: (.Contents | length)}')
echo "$health"
check "the old index still loads" Ok "$(echo "$health" | jq -r .Health)"
check "no stub went away with the upgrade" "$stubs_before" "$(stubs upgrade | wc -l | tr -d ' ')"
check "the old stub is still the same item" "$stub3" "$(episode_id upgrade "S01E03.strm")"

echo "--- after a sync and a scan"
sync_and_scan upgrade
sync_and_scan upgrade
check "partial IDs are one film now" 1 "$(film_rows upgrade 'JM ID Parziali')"
check "the merged stub is gone" 1 "$(stubs upgrade | grep -c 'JM ID Parziali')"
survivor=$(items upgrade Movie | jq -r '[.Items[] | select(.Name == "JM ID Parziali") | .Id] | first')
check "the survivor has the state of both" '{"played":true,"favorite":true}' "$(get upgrade "/Users/$uid/Items/$survivor" | jq -c '{played: .UserData.Played, favorite: .UserData.IsFavorite}')"
check "titles merged on title and year alone split" 2 "$(( $(film_rows upgrade 'La La Sonda Muta') + $(film_rows upgrade 'Sonda Muta') ))"
check "a stub of a double keeps its path and its item" "$stub3" "$(episode_id upgrade "S01E03.strm")"
check "and gets its range" '{"E":3,"End":4}' "$(get upgrade "/Users/$uid/Items/$stub3" | jq -c '{E:.IndexNumber,End:.IndexNumberEnd}')"
check "and keeps its state" true "$(get upgrade "/Users/$uid/Items/$stub3" | jq .UserData.Played)"
echo "series as a client sees it: $(doppia upgrade)"
stubs upgrade | grep "JM Serie Doppia " | sed 's/.*\///'
exit "${FAILED:-0}"
