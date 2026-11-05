#!/usr/bin/env bash
# Fresh private servers for decision 20, with what the media matrix lacks: a chain of contradicting IDs across the
# remotes, a pair without IDs whose .nfo can change, and two local films of one title.
# usage: [MEDIA=<media folder>] [WORK=<scratch folder>] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

SD="$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
HD="$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"

docker network create "$NETWORK" >/dev/null 2>&1 || true
start stock
start peer
install_plugin peer
configure peer true
share_all peer

# A(1, a) and C(2, b) on the stock server, B(1, b) on the peer. The lowest key is A's, and the best quality is B's.
add_clip stock "$SD" "JM Catena Uno (2026)" "JM Catena Uno" 999100001 tt99100001 "Primo anello."
add_clip stock "$SD" "JM Catena Tre (2026)" "JM Catena Tre" 999100002 tt99100002 "Terzo anello."
add_clip peer "$HD" "JM Catena Due (2026)" "JM Catena Due" 999100001 tt99100002 "Secondo anello."
add_clip stock "$SD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Un pianista insegue un sogno tra le colline."
add_clip peer "$HD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Una spedizione polare perde i contatti con la base."
add_clip stock "$SD" "JM Sonda Gemella (2026)" "JM Sonda Gemella" "" "" "Un pianista insegue un sogno tra le colline."
add_clip peer "$HD" "JM Sonda Gemella (2026)" "JM Sonda Gemella" "" "" "Una spedizione polare perde i contatti con la base."
scan stock
scan peer

start fresh
install_plugin fresh
configure fresh true
add_clip fresh "$SD" "JM Due Locali (2026)" "JM Due Locali" 999100011 tt99100011 "Una copia."
add_clip fresh "$SD" "JM Due Locali Bis (2026)" "JM Due Locali" 999100012 tt99100012 "Un'altra copia."
scan fresh
add_remote fresh stock http://jm-merge-s:8096 >"$WORK/id-fresh-stock"
add_remote fresh peer http://jm-merge-j:8096 >"$WORK/id-fresh-peer"

check "the peer is JellyMesh" JellyMesh "$(get fresh /JellyMesh/Admin/Status | jq -r --arg i "$(cat "$WORK/id-fresh-peer")" '.Servers[] | select(.Id == $i) | .Mode')"
exit "${FAILED:-0}"
