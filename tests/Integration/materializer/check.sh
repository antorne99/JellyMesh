#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
echo "log lines with a remote password (this run, s5): $(applog | grep -c -E 'jellymesh-test' || true)"
echo "NeedsRefresh: $(docker exec "$NAME" cat /config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json | jq -c '[.Series[].NeedsRefresh]')"
# The hosts and ports of both remotes, dots escaped, as one grep -E pattern.
addr_pat() {
  local u hp pat=
  for u in "$STANDARD_URL" "$JELLYMESH_URL"; do
    hp=${u#*://}; hp=${hp%%/*}
    pat+="${pat:+|}$(printf '%s' "${hp%:*}" | sed 's/\./\\./g')|:${hp##*:}\\b"
  done
  printf '%s' "$pat"
}
pat=$(addr_pat)
echo "index or registry with an address: $(docker exec "$NAME" sh -c 'cat /config/plugins/Jellyfin.Plugin.JellyMesh/*.json' | grep -c -E "$pat" || true)"
echo "files in the folder with an address or port: $(docker exec "$NAME" sh -c 'grep -rlE "$1" /federata --include=*.nfo --include=*.strm | wc -l' sh "$pat")"
