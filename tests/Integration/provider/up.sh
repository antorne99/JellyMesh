#!/usr/bin/env bash
# Fresh private servers with the plugin installed. Each server gets the media of one remote. With STOCK=own, the private
# stock remote too. usage: MEDIA=<main checkout>/tests/Integration/media WORK=<scratch folder> up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

rm -f "$WORK"/client-* "$WORK"/open-* "$WORK"/ws-* "$WORK"/seen-* "$WORK"/gate-*

stock_up
start a remoto-standard
start b remoto-jellymesh
install_plugin a
install_plugin b

for s in a b; do
  req "$s" PUT /JellyMesh/Admin/Settings '{"FederatedFolderPath":"/federata"}' >/dev/null
done

get a /Plugins | jq -c '.[] | select(.Name == "JellyMesh") | {Name, Version, Status}'
get b /Plugins | jq -c '.[] | select(.Name == "JellyMesh") | {Name, Version, Status}'
