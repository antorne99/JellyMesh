#!/usr/bin/env bash
# No remote token or address in a .strm, a log or anything a browser received. Run last, with the remote up or stopped.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

scan=$WORK/leak
rm -rf "$scan"
mkdir -p "$scan/remote-db" "$scan/logs" "$scan/strm"
docker cp jm-play-remote:/config/data/jellyfin.db "$scan/remote-db/"
docker cp jm-play-remote:/config/data/jellyfin.db-wal "$scan/remote-db/" 2>/dev/null || true
docker cp jm-play-local:/config/plugins/Jellyfin.Plugin.JellyMesh/federated-stubs.json "$scan/"
docker cp jm-play-local:/config/log "$scan/logs/"
docker logs jm-play-local >"$scan/logs/stdout.log" 2>&1
docker exec jm-play-local sh -c "find /federata -name '*.strm' -exec cat {} + ; echo" >"$scan/strm/all.txt"
docker exec jm-play-local sh -c "find /federata -name '*.nfo' -exec cat {} +" >"$scan/strm/nfo.txt"
wc -c "$scan/strm/all.txt"
python3 "$S/leak.py" "$scan" "$scan/logs" "$scan/strm" "$WORK/web"
