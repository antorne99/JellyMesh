#!/usr/bin/env bash
# Task 862: a stock route that names a stub by videoId or as the version it picks, also in the legacy params, wants a
# local user who can see that stub, as itemId does. Without it, anyone could ask for an attachment of a stub, which
# makes stock refresh the stub.
# Builds its own pair with the plugin of this checkout: PREFIX and PORT keep it off the jm-play-* pair.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-play-guard] [PORT=18436] [UP=0] [DOWN=0] version-guard.sh
set -euo pipefail
PREFIX=${PREFIX:-jm-play-guard}
PORT=${PORT:-18436}
WORK=${WORK:-${TMPDIR:-/tmp}/$PREFIX-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

port_of() { case $1 in local) echo "$PORT" ;; remote) echo $((PORT + 1)) ;; esac; }
name_of() { echo "$PREFIX-$1"; }
down() {
  docker rm -f "$PREFIX-local" "$PREFIX-remote" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" "$PREFIX-media" \
    "$PREFIX-remote-config" "$PREFIX-remote-cache" "$PREFIX-remote-fed" >/dev/null 2>&1 || true
}

FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
TITLE="Tears of Steel"

up() {
  down
  docker volume create "$PREFIX-media" >/dev/null

  start remote "$MEDIA/remoto-standard:/media:ro"
  library remote Film movies /media/film
  user remote jellymesh >/dev/null
  scan remote

  start local "$PREFIX-media:/media"
  docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film "/media/privata/JM Privato (2026)" && touch /media/film/.keep'
  docker exec "$PREFIX-local" "$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i testsrc=d=10:s=320x180:r=25 \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p "/media/privata/JM Privato (2026)/JM Privato (2026).mkv"
  library local Film movies /media/film /federata/Film
  library local Privato movies /media/privata
  user local viewer >/dev/null
  user local outsider >/dev/null
  only viewer Film
  only outsider Privato

  bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
  sleep 3
  wait_ready local
  login local
  settings local '.FederatedFolderPath = "/federata"' >/dev/null
  import_all local "$(add_remote local remote "http://host.docker.internal:$((PORT + 1))" jellymesh)"
  sync_now local
  scan local
}

only() { # only <user> <library>: the user sees that library alone
  local lib uid policy
  lib=$(get local /Library/VirtualFolders | jq -r --arg n "$2" '.[] | select(.Name == $n) | .ItemId')
  uid=$(user_id local "$1")
  policy=$(get local "/Users/$uid" | jq -c --arg f "$lib" '.Policy | .EnableAllFolders = false | .EnabledFolders = [$f]')
  post local "/Users/$uid/Policy" "$policy" >/dev/null
}

as() { # as <user|anonymous> <path> [curl options]: prints the status, the body is in $WORK/as-body
  local who=$1 path=$2 token=""
  shift 2
  [ "$who" != anonymous ] && token=$(curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"$who\", DeviceId=\"jm-play-$who\", Version=\"0.1\"" -d "{\"Username\":\"$who\",\"Pw\":\"jellymesh-test\"}" \
    "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken)
  curl -sS -o "$WORK/as-body" -w '%{http_code}' "$@" ${token:+-H "Authorization: MediaBrowser Token=\"$token\""} \
    "http://127.0.0.1:$PORT$path"
}

refreshed() { # refreshed <item>: when the server last refreshed it, read from a copy of its database
  local copy f
  copy=$(mktemp -d)
  for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do docker cp "$PREFIX-local:/config/data/$f" "$copy/$f" >/dev/null 2>&1 || true; done
  sqlite3 "$copy/jellyfin.db" "select DateLastRefreshed from BaseItems where lower(replace(Id, '-', '')) = '$1';"
  rm -rf "$copy"
}

[ "${UP:-1}" = 1 ] && up
login local

stub=$(stub_item local "$TITLE")
private=$(local_item local "JM Privato")
stub_n=${stub//-/}
private_n=${private//-/}
check "the remote film is a stub" 1 "$([ -n "$stub" ] && echo 1 || echo 0)"
check "the private film is local" 1 "$([ -n "$private" ] && echo 1 || echo 0)"

# The attachment route has no [Authorize] and names the item videoId. Stock refreshes a .strm before it looks.
attachment="/Videos/$stub/$stub_n/Attachments/0"
before=$(refreshed "$stub_n")
check "an anonymous attachment of a stub is refused" 401 "$(as anonymous "$attachment")"
sleep 1
check "and the stub is not refreshed for it" "$before" "$(refreshed "$stub_n")"
check "an attachment of a stub the user cannot see is not found" 404 "$(as outsider "$attachment")"
check "an attachment of a stub the user sees goes to stock" 404 "$(as viewer "$attachment")"
check "which answers that a stub has none" 1 "$(grep -c 'has no attachment' "$WORK/as-body" || true)"

trickplay="/Videos/$stub/Trickplay/320/tiles.m3u8"
check "an anonymous trickplay playlist of a stub is refused" 401 "$(as anonymous "$trickplay")"
check "a trickplay playlist of a stub the user cannot see is not found" 404 "$(as outsider "$trickplay")"
check "a trickplay playlist of a stub the user sees goes to stock" 404 "$(as viewer "$trickplay")"

# The stub as the version of an item that is not one, on routes that take it as text.
version="/Videos/$private/stream?static=true&mediaSourceId=$stub_n"
check "an anonymous stream with a stub as its version is refused" 401 "$(as anonymous "$version" -H 'Range: bytes=0-99')"
check "a stream with a stub the user cannot see as its version is not found" 404 "$(as outsider "$version" -H 'Range: bytes=0-99')"
subtitle="/Videos/$private/$stub_n/Subtitles/0/Stream.srt"
check "an anonymous subtitle with a stub as its version is refused" 401 "$(as anonymous "$subtitle")"
check "a subtitle with a stub the user cannot see as its version is not found" 404 "$(as outsider "$subtitle")"
# Stock applies the legacy params first, and their field 2 picks the version over mediaSourceId.
legacy="/Videos/$private/stream?static=true&mediaSourceId=$private_n&params=;;$stub_n"
check "an anonymous stream with a stub as its version in the legacy params is refused" 401 "$(as anonymous "$legacy" -H 'Range: bytes=0-99')"
check "a stream with a hidden stub as its version in the legacy params is not found" 404 "$(as outsider "$legacy" -H 'Range: bytes=0-99')"

stream="/Videos/$stub/stream?static=true&mediaSourceId=$stub_n"
check "an anonymous stream of a stub is refused" 401 "$(as anonymous "$stream" -H 'Range: bytes=0-99')"
check "a stream of a stub the user cannot see is not found" 404 "$(as outsider "$stream" -H 'Range: bytes=0-99')"
check "a stream of a stub the user sees is served" 206 "$(as viewer "$stream" -H 'Range: bytes=0-99')"
check "a stream of a local film with its own version goes to stock" 206 "$(as outsider "/Videos/$private/stream?static=true&mediaSourceId=$private_n" -H 'Range: bytes=0-99')"

[ "${DOWN:-0}" = 1 ] && down
exit $FAILED
