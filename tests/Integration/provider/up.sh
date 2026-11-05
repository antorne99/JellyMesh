#!/usr/bin/env bash
# Fresh private servers with the plugin installed, and the gates between them. Each server gets the media of one remote.
# usage: MEDIA=<main checkout>/tests/Integration/media WORK=<scratch folder> up.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

rm -f "$WORK"/client-* "$WORK"/open-* "$WORK"/ws-* "$WORK"/seen-* "$WORK"/gate-*

start a remoto-standard
start b remoto-jellymesh
install_plugin a
install_plugin b

for s in a b; do
  cfg=$(get "$s" "/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration" | jq '.FederatedFolderPath = "/federata"')
  post "$s" "/Plugins/28c43ea1-fb5a-4b3a-8e6e-30aa3245c653/Configuration" "$cfg" >/dev/null
done

get a /Plugins | jq -c '.[] | select(.Name == "JellyMesh") | {Name, Version, Status}'
get b /Plugins | jq -c '.[] | select(.Name == "JellyMesh") | {Name, Version, Status}'
