#!/usr/bin/env bash
# Removes the private servers, their volumes and their network.
set -uo pipefail
for name in jm-dedup-f jm-dedup-s jm-dedup-j jm-dedup-u; do
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
docker network rm jm-dedup-net >/dev/null 2>&1
exit 0
