#!/usr/bin/env bash
# Removes what up.sh made: containers, volumes, network, the Playwright volume and the scratch folder.
# usage: UI_PREFIX=<name> UI_PORT=<port> down.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

for s in $SERVERS; do
  name=$(container_of "$s")
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
docker volume rm "$UI_PREFIX-pw" >/dev/null 2>&1
docker network rm "$NETWORK" >/dev/null 2>&1
rm -rf "$WORK"
exit 0
