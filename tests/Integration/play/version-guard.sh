#!/usr/bin/env bash
# Task 862: a stock route that names a stub by videoId or as the version it picks, also in the legacy params, wants a
# local user who can see that stub, as itemId does. Without it, anyone could ask for an attachment of a stub, which
# makes stock refresh the stub. Then the reuse of a local play session over a real socket (D10, D11): the remote user
# cannot transcode, so the stub plays through a local transcode with a stock play session.
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
  local ruid policy
  ruid=$(user_id remote jellymesh)
  policy=$(get remote "/Users/$ruid" | jq -c '.Policy | .EnableVideoPlaybackTranscoding = false | .EnableAudioPlaybackTranscoding = false | .EnablePlaybackRemuxing = false')
  post remote "/Users/$ruid/Policy" "$policy" >/dev/null
  scan remote

  start local "$PREFIX-media:/media"
  docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film "/media/privata/JM Privato (2026)" && touch /media/film/.keep'
  docker exec "$PREFIX-local" "$FFMPEG" -hide_banner -loglevel error -y -f lavfi -i testsrc=d=10:s=320x180:r=25 \
    -c:v libx264 -preset ultrafast -pix_fmt yuv420p "/media/privata/JM Privato (2026)/JM Privato (2026).mkv"
  library local Film movies /media/film /federata/Film
  library local Privato movies /media/privata
  user local viewer >/dev/null
  user local viewer2 >/dev/null
  user local outsider >/dev/null
  only viewer Film
  only viewer2 Film
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
  [ "$who" != anonymous ] && token=$(token_for "$who")
  curl -sS -o "$WORK/as-body" -w '%{http_code}' "$@" ${token:+-H "Authorization: MediaBrowser Token=\"$token\""} \
    "http://127.0.0.1:$PORT$path"
}

token_for() { # token_for <user>: one login per user and run, so that a flood of requests opens no flood of sessions
  [ -s "$WORK/tok-$1" ] || curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"$1\", DeviceId=\"jm-play-$1\", Version=\"0.1\"" -d "{\"Username\":\"$1\",\"Pw\":\"jellymesh-test\"}" \
    "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/tok-$1"
  cat "$WORK/tok-$1"
}

refreshed() { # refreshed <item>: when the server last refreshed it, read from a copy of its database
  local copy f
  copy=$(mktemp -d)
  for f in jellyfin.db jellyfin.db-wal jellyfin.db-shm; do docker cp "$PREFIX-local:/config/data/$f" "$copy/$f" >/dev/null 2>&1 || true; done
  sqlite3 "$copy/jellyfin.db" "select DateLastRefreshed from BaseItems where lower(replace(Id, '-', '')) = '$1';"
  rm -rf "$copy"
}

[ "${UP:-1}" = 1 ] && up
rm -f "$WORK"/tok-*
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

# Integration test 5: the play session of a local transcode of the stub, used by another user over a real socket.
open_local() { # open_local <user>: opens the stub through the local transcode, prints its play session
  as "$1" "/Items/$stub/PlaybackInfo?userId=$(user_id local "$1")" -X POST -H "$ACCEPT" -H 'Content-Type: application/json' \
    -d "$PROFILE_HLS" >/dev/null
  jq -r '.PlaySessionId // ""' "$WORK/as-body"
}
transcode_url() { jq -r '.MediaSources[0].TranscodingUrl // ""' "$WORK/as-body"; }
transcodes() { # how many transcode jobs the local server keeps, by the hash their files share: stock deletes them on a kill
  # ffmpeg itself ends as soon as it encoded the one-minute film, while its job lives on until it is killed or idle.
  docker exec "$PREFIX-local" sh -c 'ls /cache/transcodes 2>/dev/null | cut -c1-32 | sort -u | wc -l'
}
keep_job() { as viewer "/Sessions/Playing/Ping?playSessionId=$psid" -X POST >/dev/null; } # the stock job idles out after 60 s
through_private() { # through_private <user> <play session>: a stream of the private film that names the play session
  as "$1" "/Videos/$private/stream?static=true&mediaSourceId=$private_n&playSessionId=$2" -H 'Range: bytes=0-99'
}
stop_report() { # stop_report <user> <play session>
  as "$1" /Sessions/Playing/Stopped -X POST -H 'Content-Type: application/json' -d "{\"ItemId\":\"$stub\",\"PlaySessionId\":\"$2\"}"
}
kill_encodings() { # kill_encodings <user> <play session>: DELETE ActiveEncodings, with the device of the owner
  as "$1" "/Videos/ActiveEncodings?deviceId=jm-play-viewer&playSessionId=$2" -X DELETE
}

psid=$(open_local viewer)
master=$(transcode_url)
query=${master#*\?}
check "the stub opens through a local transcode with a stock play session" 1 \
  "$([ -n "$psid" ] && echo "$master" | grep -qi "^/videos/[0-9a-f-]*/master.m3u8?.*PlaySessionId=$psid" && echo 1 || echo 0)"
check "its owner reads the master playlist" 200 "$(as viewer "/videos/$stub/master.m3u8?$query")"
variant=$(grep -v '^#' "$WORK/as-body" | head -1)
check "and the variant playlist" 200 "$(as viewer "/videos/$stub/$variant")"
segment=$(grep -v '^#' "$WORK/as-body" | head -1)
check "and the first segment" 200 "$(as viewer "/videos/$stub/$segment")"
check "its transcode job runs" 1 "$(transcodes)"

check "another user who sees the stub cannot stream with that play session" 404 \
  "$(as viewer2 "/Videos/$stub/stream?static=true&mediaSourceId=$stub_n&playSessionId=$psid" -H 'Range: bytes=0-99')"
check "nor read its master playlist" 404 "$(as viewer2 "/videos/$stub/master.m3u8?$query")"
check "nor one of its segments" 404 "$(as viewer2 "/videos/$stub/${segment/0.ts/1.ts}")"
check "nor reach it through a film of their own" 404 "$(through_private outsider "$psid")"
check "anonymous cannot reach it either" 401 "$(through_private anonymous "$psid")"
check "another user's DELETE ActiveEncodings is a 204" 204 "$(kill_encodings viewer2 "$psid")"
check "another user's stop report is a 204" 204 "$(stop_report viewer2 "$psid")"
sleep 2
check "and its transcode job is still there for its owner" 1 "$(transcodes)"
check "whose next segment is served" 200 "$(as viewer "/videos/$stub/${segment/0.ts/1.ts}")"
# The legacy segment route belongs to D9 (F20): recorded here, not checked.
playlist=$(docker exec "$PREFIX-local" sh -c 'ls /cache/transcodes 2>/dev/null | cut -c1-32 | head -1')
legacy="/Videos/$stub/hls/$playlist/${playlist}0.ts"
echo "info legacy segment route: anonymous $(as anonymous "$legacy"), another user $(as viewer2 "$legacy")"

# D11: more than 256 play sessions of the owner, while the first stays in use.
flood=$WORK/flood
: >"$flood"
for i in $(seq 1 300); do
  open_local viewer >>"$flood"
  [ $((i % 25)) = 0 ] && keep_job
done
check "the owner opened 300 more play sessions, all different" 300 "$(sort -u "$flood" | grep -c . || true)"
check "none of them is the first" 0 "$(grep -c -x "$psid" "$flood" || true)"
check "after them the first is still out of reach of another user" 404 "$(through_private outsider "$psid")"

# D10: the stub is renamed, so that its item is gone, while its play session is open.
strm=$(docker exec "$PREFIX-local" sh -c "find /federata/Film -name '*.strm' | grep -F \"$TITLE\" | head -1")
folder=$(dirname "$strm")
keep_job
docker exec "$PREFIX-local" mv "$folder" "$folder (rinominata)"
scan local
keep_job
check "after the scan the old stub is gone" 404 "$(as viewer "/Items/$stub")"
check "its play session is still out of reach of another user" 404 "$(through_private outsider "$psid")"
check "and of anonymous" 401 "$(through_private anonymous "$psid")"
check "another user's DELETE ActiveEncodings is still a 204" 204 "$(kill_encodings outsider "$psid")"
sleep 2
check "and the transcode job is still there" 1 "$(transcodes)"
echo "info the owner's next segment after the rename: $(as viewer "/videos/$stub/${segment/0.ts/2.ts}")"
docker exec "$PREFIX-local" mv "$folder (rinominata)" "$folder"
scan local
keep_job
stub=$(stub_item local "$TITLE")
stub_n=${stub//-/}

# D11: the owner's stop ends the record.
check "the owner's DELETE ActiveEncodings is served" 204 "$(kill_encodings viewer "$psid")"
sleep 2
check "and kills the transcode job" 0 "$(transcodes)"
check "after it the play session is out of the record" 206 "$(through_private outsider "$psid")"
quit=$(open_local viewer)
check "the owner's stop report is served" 204 "$(stop_report viewer "$quit")"
check "and takes its play session out of the record" 206 "$(through_private outsider "$quit")"

# D11: a play session left idle leaves the record, one its owner keeps reporting stays.
settings local '.PlaybackIdleSeconds = 30' >/dev/null
idle=$(open_local viewer)
kept=$(open_local viewer)
check "a fresh play session is out of reach of another user" 404 "$(through_private outsider "$idle")"
for i in 1 2 3 4 5; do
  as viewer "/Sessions/Playing/Ping?playSessionId=$kept" -X POST >/dev/null
  sleep 10
done
check "after the idle time an unused play session is out of the record" 206 "$(through_private outsider "$idle")"
check "one its owner kept reporting is still guarded" 404 "$(through_private outsider "$kept")"
settings local '.PlaybackIdleSeconds = 90' >/dev/null

[ "${DOWN:-0}" = 1 ] && down
exit $FAILED
