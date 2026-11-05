#!/usr/bin/env bash
# Removes the private servers, their volumes, their network and the scratch folder.
set -uo pipefail
for name in jm-merge-f jm-merge-s jm-merge-j; do
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
docker volume rm jm-merge-pw >/dev/null 2>&1
docker network rm jm-merge-net >/dev/null 2>&1
rm -rf "${WORK:-${TMPDIR:-/tmp}/jm-merge-work}"
exit 0
