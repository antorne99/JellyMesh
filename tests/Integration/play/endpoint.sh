#!/usr/bin/env bash
# Decision 17: the endpoint of a .strm wants the loopback socket and the signature, and a leaked signature is worth nothing
# off the host. Run after upgrade.sh.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

login local
tears=$(stub_item local "Tears of Steel")
url=$(strm_text local "Tears of Steel")
path=${url#http://127.0.0.1:8096}
bare=${path%%\?*}
inside() { docker exec "$PREFIX-local" curl -s -o /dev/null -w '%{http_code}' "$@"; }

check "loopback with the signature is served" 206 "$(inside -H 'Range: bytes=0-99' "http://127.0.0.1:8096$path")"
check "HEAD too" 200 "$(inside -I "http://127.0.0.1:8096$path")"
check "loopback without a signature is refused" 403 "$(inside "http://127.0.0.1:8096$bare")"
check "loopback with a wrong signature is refused" 403 "$(inside "http://127.0.0.1:8096$bare?sig=0000")"
other=$(strm_text local "Sonda Senza ID")
check "the signature of another stub is refused" 403 "$(inside "http://127.0.0.1:8096$bare?sig=${other##*sig=}")"
check "a forwarded request with the signature is refused" 403 "$(inside -H 'X-Forwarded-For: 203.0.113.9' "http://127.0.0.1:8096$path")"
check "an unknown stub with a bad signature tells nothing" 403 "$(inside "http://127.0.0.1:8096/JellyMesh/Play/unknown?sig=0000")"
check "a client off the host with the signature is refused" 403 "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT$path")"

# Decision 17 confirmed the stock routes for who is signed in and sees the title: they reach the .strm the same way.
viewer_login
check "a stock route opens the signed stub for a user" 206 "$(curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "http://127.0.0.1:$PORT/Videos/$tears/stream?static=true&ApiKey=$(cat "$WORK/token-viewer")")"
check "and refuses without a user" 401 "$(curl -s -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "http://127.0.0.1:$PORT/Videos/$tears/stream?static=true")"

# What decision 17 accepted: a user who can list the item can read the signature. It must grant nothing.
as_viewer GET "/Items?Ids=$tears&Fields=MediaSources" >/dev/null
leaked=$(jq -r '.Items[0].MediaSources[0].Path' "$WORK/viewer-body")
check "the viewer can read the signed URL" "$url" "$leaked"
check "and it opens nothing from where the viewer is" 403 "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT${leaked#http://127.0.0.1:8096}")"

# The key survives a restart.
before=$(docker exec "$PREFIX-local" sh -c "stat -c %Y \"\$(find /federata/Film -name '*.strm' | grep Tears)\"")
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "the signature still opens after a restart" 206 "$(inside -H 'Range: bytes=0-99' "http://127.0.0.1:8096$path")"
sync_now local
check "a sync after a restart rewrites nothing" "$before" "$(docker exec "$PREFIX-local" sh -c "stat -c %Y \"\$(find /federata/Film -name '*.strm' | grep Tears)\"")"
exit $FAILED
