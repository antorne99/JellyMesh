#!/usr/bin/env bash
# Removes the private servers, their volumes and their network.
set -uo pipefail
PREFIX=${PREFIX:-jm-dedup}
for name in "$PREFIX-f" "$PREFIX-s" "$PREFIX-j" "$PREFIX-u"; do
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
docker network rm "$PREFIX-net" >/dev/null 2>&1
exit 0
