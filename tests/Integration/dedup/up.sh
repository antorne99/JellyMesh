#!/usr/bin/env bash
# Fresh private servers: the two remotes, and the consumer that starts with the current build. The consumer gives both
# servers the library mode "None" and the admin adds their folders to its Film and Serie libraries by hand (row 443).
# usage: [MEDIA=<media folder>] [WORK=<scratch folder>] up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

docker network create "$NETWORK" >/dev/null 2>&1 || true
start stock
start peer
install_plugin peer
configure peer true
share_all peer

start fresh per-server
install_plugin fresh
configure fresh true
add_remote fresh stock "http://$(container_of stock):8096" None >"$WORK/id-fresh-stock"
add_remote fresh peer "http://$(container_of peer):8096" None >"$WORK/id-fresh-peer"

check "the stock remote is standard" Standard "$(get fresh /JellyMesh/Admin/Status | jq -r --arg i "$(cat "$WORK/id-fresh-stock")" '.Servers[] | select(.Id == $i) | .Mode')"
check "the peer is JellyMesh" JellyMesh "$(get fresh /JellyMesh/Admin/Status | jq -r --arg i "$(cat "$WORK/id-fresh-peer")" '.Servers[] | select(.Id == $i) | .Mode')"
check "both servers have the library mode None" '["None","None"]' "$(get fresh /JellyMesh/Admin/Status | jq -c '[.Servers[].AutomaticLibraries.Mode]')"
sync_now fresh
link_folders fresh
check "the plugin made no library" '["Film","Serie"]' "$(get fresh /Library/VirtualFolders | jq -c '[.[].Name] | sort')"
check "each library holds the folders of both servers" '{"Film":2,"Serie":2}' \
  "$(get fresh /Library/VirtualFolders | jq -c 'map({(.Name): ([.Locations[] | select(startswith("/federata/"))] | length)}) | add')"
scan fresh
settle fresh
exit "${FAILED:-0}"
