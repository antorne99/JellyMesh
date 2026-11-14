#!/usr/bin/env bash
# Fresh private servers for the checks of the configuration page, with the plugin built from this checkout.
# usage: UI_PREFIX=<name> UI_PORT=<port> [MEDIA=<media folder>] [WORK=<scratch folder>] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

SD="$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
HD="$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
[ -f "$SD" ] && [ -f "$HD" ] || { echo "no media in $MEDIA: run scripts/prepare-media.sh" >&2; exit 1; }

docker network create "$NETWORK" >/dev/null 2>&1 || true
start stock
start peer
flat_paths_off peer
install_plugin peer
configure peer true
share_all peer

# Doubtful films that need no new clips: a chain of contradicting IDs, and a pair that nothing confirms.
add_clip stock "$SD" "JM Catena Uno (2026)" "JM Catena Uno" 999100001 tt99100001 "Primo anello."
add_clip stock "$SD" "JM Catena Tre (2026)" "JM Catena Tre" 999100002 tt99100002 "Terzo anello."
add_clip peer "$HD" "JM Catena Due (2026)" "JM Catena Due" 999100001 tt99100002 "Secondo anello."
add_clip stock "$SD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Un pianista insegue un sogno tra le colline."
add_clip peer "$HD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Una spedizione polare perde i contatti con la base."
scan stock
scan peer

start fresh
flat_paths_off fresh
install_plugin fresh
configure fresh true
add_remote fresh stock "http://$(container_of stock):8096" >"$WORK/id-fresh-stock"
add_remote fresh peer "http://$(container_of peer):8096" >"$WORK/id-fresh-peer"
sync_and_scan fresh
# The page is checked on the layout of 0.5: the folders of each server in the libraries, with no migration to do.
link_server_folders fresh
scan fresh
settle fresh
# The user of a connected server, and one left free for the form that connects the next.
share_all fresh
post fresh /Users/New '{"Name":"friend","Password":"jellymesh-test"}' >/dev/null

start first
flat_paths_off first
install_plugin first

status=$(get fresh /JellyMesh/Admin/Status)
check "the stock remote is standard" Standard "$(jq -r --arg i "$(cat "$WORK/id-fresh-stock")" '.Servers[] | select(.Id == $i) | .Mode' <<<"$status")"
check "the peer is JellyMesh" JellyMesh "$(jq -r --arg i "$(cat "$WORK/id-fresh-peer")" '.Servers[] | select(.Id == $i) | .Mode' <<<"$status")"
check "the catalog is synced" true "$(get fresh /JellyMesh/Admin/Sync | jq '[.Servers[].Movies > 0] | all')"
check "there are doubtful films" true "$(list fresh | jq '.Cases | length > 0')"
check "the folders of each server are linked" Linked "$(jq -r .FederatedFolder.Link <<<"$status")"
check "there is no migration to do" null "$(jq -c .FederatedFolder.Migration <<<"$status")"
check "a server is connected" jellymesh "$(get fresh /JellyMesh/Admin/Peers | jq -r '.[0].UserName')"
check "the first server has no servers" 0 "$(get first /JellyMesh/Admin/Status | jq '.Servers | length')"
exit "$FAILED"
