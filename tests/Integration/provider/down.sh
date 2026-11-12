#!/usr/bin/env bash
# Removes the private servers, their volumes and the gates, and the private stock server of STOCK=own.
# usage: MEDIA=... WORK=... down.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
gates_down
# Live logins, the one of the shared stock remote among them.
rm -f "$WORK"/gate-*.tokens "$WORK"/leaks.tokens
for s in a b; do
  name=$(container_of "$s")
  docker rm -f "$name" >/dev/null 2>&1
  docker volume rm "$name-config" "$name-cache" "$name-fed" "$name-extra" >/dev/null 2>&1
done
stock_down
docker ps -a --format '{{.Names}}' | grep "^$PREFIX" || echo "no $PREFIX containers left"
docker volume ls --format '{{.Name}}' | grep "^$PREFIX" || echo "no $PREFIX volumes left"
