#!/usr/bin/env bash
# Fresh private servers: the two remotes, and the consumer that starts with the current build.
# usage: [MEDIA=<media folder>] [WORK=<scratch folder>] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

docker network create "$NETWORK" >/dev/null 2>&1 || true
start stock
start peer
install_plugin peer
configure peer true
share_all peer

start fresh
install_plugin fresh
configure fresh true
add_remote fresh stock http://jm-dedup-s:8096 >"$WORK/id-fresh-stock"
add_remote fresh peer http://jm-dedup-j:8096 >"$WORK/id-fresh-peer"

check "the stock remote is standard" Standard "$(get fresh /JellyMesh/Admin/Status | jq -r --arg i "$(cat "$WORK/id-fresh-stock")" '.Servers[] | select(.Id == $i) | .Mode')"
check "the peer is JellyMesh" JellyMesh "$(get fresh /JellyMesh/Admin/Status | jq -r --arg i "$(cat "$WORK/id-fresh-peer")" '.Servers[] | select(.Id == $i) | .Mode')"
exit "${FAILED:-0}"
