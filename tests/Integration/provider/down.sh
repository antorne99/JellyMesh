#!/usr/bin/env bash
# Removes the private servers, their volumes and the gates.
# usage: MEDIA=... WORK=... down.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
pkill -f "provider/gate.py" 2>/dev/null
# Live logins, the one of the shared stock remote among them.
rm -f "$WORK"/gate-*.tokens "$WORK"/leaks.tokens
for s in a b; do
  name=$(container_of "$s")
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
docker ps -a --format '{{.Names}}' | grep '^jm-prov' || echo "no jm-prov containers left"
docker volume ls --format '{{.Name}}' | grep '^jm-prov' || echo "no jm-prov volumes left"
