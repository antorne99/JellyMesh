#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
echo "log lines with remote address, port or password (this run, s5): $(applog | grep -c -E 'host\.docker\.internal|:809[78]|jellymesh-test' || true)"
echo "NeedsRefresh: $(docker exec "$NAME" cat /config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json | jq -c '[.Series[].NeedsRefresh]')"
echo "index or registry with an address: $(docker exec "$NAME" sh -c 'cat /config/plugins/Jellyfin.Plugin.JellyMesh/*.json' | grep -c -E 'host\.docker\.internal' || true)"
echo "files in the folder with an address or port: $(docker exec "$NAME" sh -c 'grep -rlE "host\.docker\.internal|:809[78]" /federata --include=*.nfo --include=*.strm | wc -l')"
