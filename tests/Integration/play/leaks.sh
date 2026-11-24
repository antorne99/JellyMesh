#!/usr/bin/env bash
# No remote token in a .strm, a log or anything a browser received, and no remote address but in the logs. Run last, with the remote up or stopped.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

scan=$WORK/leak
rm -rf "$scan"
mkdir -p "$scan/remote-db" "$scan/plugin-db" "$scan/logs" "$scan/strm"
docker cp "$PREFIX-remote:/config/data/jellyfin.db" "$scan/remote-db/"
docker cp "$PREFIX-remote:/config/data/jellyfin.db-wal" "$scan/remote-db/" 2>/dev/null || true
docker cp "$PREFIX-local:$DATA/jellymesh.db" "$scan/plugin-db/"
docker cp "$PREFIX-local:$DATA/jellymesh.db-wal" "$scan/plugin-db/" 2>/dev/null || true
docker cp "$PREFIX-local:/config/log" "$scan/logs/"
docker logs "$PREFIX-local" >"$scan/logs/stdout.log" 2>&1
docker exec "$PREFIX-local" sh -c "find /federata -name '*.strm' -exec cat {} + ; echo" >"$scan/strm/all.txt"
docker exec "$PREFIX-local" sh -c "find /federata -name '*.nfo' -exec cat {} +" >"$scan/strm/nfo.txt"
wc -c "$scan/strm/all.txt"
REMOTE_PORT=$(port_of remote) python3 "$S/leak.py" "$scan" "$scan/logs" "$scan/strm" "$WORK/web"
