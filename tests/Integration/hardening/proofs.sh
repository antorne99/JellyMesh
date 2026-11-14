#!/usr/bin/env bash
# Whether the limits of the hardening leave real playback alone: the proofs of tests/Integration/provider, unchanged, on
# private servers. PREFIX and PORT_A move their names and ports, because they would collide with anything else using
# them, and a private stock server stands in for the shared stack. Then what those proofs do not cover about a
# peer. Takes about 15 minutes. Containers and volumes are removed at the end.
# usage: proofs.sh   (MEDIA points at tests/Integration/media of the main checkout, scratch goes in WORK;
#   PREFIX, PORT_A, STOCK_NAME and STOCK_PORT move it)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
# The media is not in the repository, so a worktree finds it in the main checkout.
if [ -z "${MEDIA:-}" ]; then
  MEDIA=$WT/tests/Integration/media
  [ -d "$MEDIA" ] || MEDIA=$(cd "$WT/../../.." && pwd)/tests/Integration/media
fi
export MEDIA
export WORK=${WORK:-${TMPDIR:-/tmp}/jm-hard-proofs}
IMAGE=jellyfin/jellyfin:12.4.20261026-211555
STOCK=${STOCK_NAME:-jm-hard-s}
STOCK_PORT=${STOCK_PORT:-18457}
ACCEPT='Accept: application/json; profile="PascalCase"'
AUTH='MediaBrowser Client="JellyMeshHard", Device="hard", DeviceId="jm-hard-stock", Version="0.1"'
FAILED=0

rm -rf "$WORK"
mkdir -p "$WORK"
export PREFIX=${PREFIX:-jm-hard-p} PORT_A=${PORT_A:-18450} STOCK_PORT
PROOFS=$WT/tests/Integration/provider

cleanup() {
  bash "$PROOFS/down.sh" >/dev/null 2>&1
  docker rm -f "$STOCK" >/dev/null 2>&1
  docker volume rm "$STOCK-config" "$STOCK-cache" >/dev/null 2>&1
}
trap cleanup EXIT

req() {
  curl -sS -X "$1" -H "$ACCEPT" -H "Authorization: $AUTH${TOKEN:+, Token=\"$TOKEN\"}" -H 'Content-Type: application/json' ${3:+-d "$3"} "http://127.0.0.1:$STOCK_PORT$2"
}

echo "--- private stock server"
docker rm -f "$STOCK" >/dev/null 2>&1
docker volume rm "$STOCK-config" "$STOCK-cache" >/dev/null 2>&1
docker run -d --name "$STOCK" -p "127.0.0.1:$STOCK_PORT:8096" -e TZ=Europe/Rome -v "$STOCK-config:/config" -v "$STOCK-cache:/cache" \
  -v "$MEDIA/remoto-standard:/media:ro" "$IMAGE" >/dev/null
until curl -fsS "http://127.0.0.1:$STOCK_PORT/System/Info/Public" 2>/dev/null | jq -e .Version >/dev/null; do sleep 1; done
sleep 5
TOKEN=
req POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null
req GET /Startup/User >/dev/null
req POST /Startup/User '{"Name":"admin","Password":"jellymesh-test"}' >/dev/null
req POST /Startup/Complete >/dev/null
TOKEN=$(req POST /Users/AuthenticateByName '{"Username":"admin","Pw":"jellymesh-test"}' | jq -r .AccessToken)
req POST /Users/New '{"Name":"jellymesh","Password":"jellymesh-test"}' >/dev/null
req POST "/Library/VirtualFolders?name=Film&collectionType=movies&refreshLibrary=true&paths=/media/film" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
req POST "/Library/VirtualFolders?name=Serie&collectionType=tvshows&refreshLibrary=true&paths=/media/serie" '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
until [ "$(req GET '/Items?Recursive=true&IncludeItemTypes=Movie' | jq -r .TotalRecordCount)" -gt 0 ]; do sleep 2; done
sleep 20

cd "$PROOFS" || exit 1
for script in up.sh federate.sh boundaries.sh feed.sh limits.sh network.sh; do
  echo "=== $script"
  bash "./$script" >"$WORK/out-$script" 2>&1
  code=$?
  grep -E '^FAIL' "$WORK/out-$script"
  echo "$script: exit $code, $(grep -c '^PASS' "$WORK/out-$script") passed, $(grep -c '^FAIL' "$WORK/out-$script") failed"
  [ "$code" = 0 ] || FAILED=1
done
echo "=== leaks.sh"
bash ./leaks.sh "$WORK"/out-*.sh >"$WORK/out-leaks" 2>&1 || FAILED=1
grep -E '^FAIL' "$WORK/out-leaks"
echo "leaks.sh: $(grep -c '^PASS' "$WORK/out-leaks") passed, $(grep -c '^FAIL' "$WORK/out-leaks") failed"

bash ./peer-extra.sh || FAILED=1
exit "$FAILED"
