#!/usr/bin/env bash
# Row 212: the legacy audio segment and playlist routes serve a file of the transcode folder by its name alone. They are
# guarded as the legacy segment route of row 78 (D9): 401 to anonymous, and a file of the transcode job of another
# user's recorded play session reads as a missing one, with what stock answers for a file that is not there. Then the
# legacy segment route answers a refused playlist id as one that matches nothing, so a short id is no oracle of a job
# name. Then row 229: a /JellyMesh/Play session stops for its owner who is not an admin once its title is in no library.
# Builds its own pair with the plugin of this checkout: PREFIX and PORT keep it off the other stacks.
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-legacy] [PORT=18900] [UP=0] [DOWN=0] legacy-routes.sh
set -euo pipefail
PREFIX=${PREFIX:-jm-legacy}
PORT=${PORT:-18900}
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
TRANSCODES=/cache/transcodes

# up: the remote user may not transcode, so the stub plays through a local transcode (local) or the proxy (proxy).
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
  library local Film movies /media/film
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
  local id
  id=$(add_remote local remote "http://host.docker.internal:$((PORT + 1))" jellymesh)
  check "the folder of the server joins the library" 204 "$(link_folder local Film "$(server_folder "$id")/Film")"
  import_all local "$id"
  sync_now local
  scan local
  # The scan right after the sync sometimes misses the new stubs.
  local i
  for i in $(seq 1 5); do
    [ -n "$(stub_item local "$TITLE")" ] && break
    scan local
  done
}

only() { # only <user> <library>...: the user sees those libraries alone
  local user=$1 libs uid policy
  shift
  libs=$(get local /Library/VirtualFolders | jq -c --args '[.[] | select(.Name | IN($ARGS.positional[])) | .ItemId]' "$@")
  uid=$(user_id local "$user")
  policy=$(get local "/Users/$uid" | jq -c --argjson f "$libs" '.Policy | .EnableAllFolders = false | .EnabledFolders = $f')
  post local "/Users/$uid/Policy" "$policy" >/dev/null
}

token_for() { # token_for <user>: one login per user and run
  [ -s "$WORK/tok-$1" ] || curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' \
    -H "Authorization: MediaBrowser Client=\"JellyMeshPlay\", Device=\"$1\", DeviceId=\"jm-play-$1\", Version=\"0.1\"" -d "{\"Username\":\"$1\",\"Pw\":\"jellymesh-test\"}" \
    "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/tok-$1"
  cat "$WORK/tok-$1"
}

as() { # as <user|anonymous> <path> [curl options]: prints the status, the body is in $WORK/as-body
  local who=$1 path=$2 token=""
  shift 2
  [ "$who" != anonymous ] && token=$(token_for "$who")
  curl -sS -o "$WORK/as-body" -D "$WORK/as-head" -w '%{http_code}' "$@" ${token:+-H "Authorization: MediaBrowser Token=\"$token\""} \
    "http://127.0.0.1:$PORT$path"
}

shape() { # shape <user> <path>: the status, the content type and the body of the answer, on one line
  local code
  code=$(as "$1" "$2")
  printf '%s|%s|%s' "$code" "$(grep -i '^content-type:' "$WORK/as-head" | tr -d '\r' | cut -d' ' -f2- || true)" "$(head -c 200 "$WORK/as-body")"
}

open_local() { # open_local <user>: opens the stub through the local transcode, prints its play session
  as "$1" "/Items/$stub/PlaybackInfo?userId=$(user_id local "$1")" -X POST -H "$ACCEPT" \
    -H 'Content-Type: application/json' -d "$PROFILE_HLS" >/dev/null
  jq -r '.PlaySessionId // ""' "$WORK/as-body"
}
files() { docker exec "$PREFIX-local" sh -c "ls $TRANSCODES 2>/dev/null"; }
plant() { docker exec "$PREFIX-local" sh -c "printf jellymesh >$TRANSCODES/$1"; } # plant <name>: a file in the folder

[ "${UP:-1}" = 1 ] && up
rm -f "$WORK"/tok-*
login local

stub=$(stub_item local "$TITLE")
private=$(local_item local "JM Privato")
check "the remote film is a stub" 1 "$([ -n "$stub" ] && echo 1 || echo 0)"
check "the private film is local" 1 "$([ -n "$private" ] && echo 1 || echo 0)"

# What stock answers for a file that is not there, on each route, before any job runs.
none_audio=$(shape outsider "/Audio/$private/hls/jmnone/stream.aac")
none_playlist=$(shape outsider "/Videos/$private/hls/jmnone/stream.m3u8")
none_segment=$(shape outsider "/Videos/$private/hls/jmnone/jmnone0.ts")
echo "info stock for a missing audio segment: $none_audio"
echo "info stock for a missing playlist: $none_playlist"
echo "info stock for a legacy segment of no playlist: $none_segment"

jobs_before=$(files | cut -c1-32 | sort -u)
psid=$(open_local viewer)
master=$(jq -r '.MediaSources[0].TranscodingUrl // ""' "$WORK/as-body")
check "the stub opens through a local transcode with a stock play session" 1 \
  "$([ -n "$psid" ] && echo "$master" | grep -qi "PlaySessionId=$psid" && echo 1 || echo 0)"
check "its owner reads the master playlist" 200 "$(as viewer "/videos/$stub/master.m3u8?${master#*\?}")"
variant=$(grep -v '^#' "$WORK/as-body" | head -1)
check "and the variant playlist" 200 "$(as viewer "/videos/$stub/$variant")"
segment=$(grep -v '^#' "$WORK/as-body" | head -1)
check "and the first segment" 200 "$(as viewer "/videos/$stub/$segment")"
job=$(comm -13 <(echo "$jobs_before") <(files | cut -c1-32 | sort -u) | head -1)
check "its transcode job writes its segments" 32 "${#job}"

# Row 212, the playlist route: the playlist of the job, by its name, whatever item the route names. On 12.4 ffmpeg keeps
# the playlist of an HLS job off the disk, so a file at the path of the job stands in for it.
docker exec "$PREFIX-local" sh -c "printf '#EXTM3U\n' >$TRANSCODES/$job.m3u8"
playlist="/Videos/$private/hls/$job/stream.m3u8"
check "anonymous cannot read the playlist of the job" 401 "$(as anonymous "$playlist")"
check "another user cannot read it through a film of their own" "$none_playlist" "$(shape outsider "$playlist")"
check "nor another user who sees the stub through it" "$none_playlist" "$(shape viewer2 "/Videos/$stub/hls/$job/stream.m3u8")"
check "nor in upper case" "$none_playlist" "$(shape outsider "/Videos/$private/hls/$(echo "$job" | tr a-f A-F)/stream.m3u8")"
check "its owner reads it" 200 "$(as viewer "/Videos/$stub/hls/$job/stream.m3u8")"
check "which is the playlist of the job" 1 "$(grep -c '^#EXTM3U' "$WORK/as-body" || true)"
plant jmfree.m3u8
check "a playlist of no recorded job goes to stock" 200 "$(as outsider "/Videos/$private/hls/jmfree/stream.m3u8")"
check "and serves it" jellymesh "$(cat "$WORK/as-body")"

# Row 212, the audio segment route: stock writes no .aac or .mp3 for an HLS job, so a file named after the job stands
# in for one; a progressive audio transcode of the play session is tried as well.
plant "${job}7.aac"
plant "${job}.mp3"
audio="/Audio/$private/hls/${job}7/stream.aac"
check "anonymous cannot read an audio file of the job" 401 "$(as anonymous "$audio")"
check "nor a file of no recorded job" 401 "$(as anonymous "/Audio/$private/hls/jmfree/stream.aac")"
check "another user cannot read an audio file of the job" "$none_audio" "$(shape outsider "$audio")"
check "nor its .mp3" "$none_audio" "$(shape outsider "/Audio/$private/hls/$job/stream.mp3")"
check "nor another user who sees the stub" "$none_audio" "$(shape viewer2 "/Audio/$stub/hls/${job}7/stream.aac")"
check "its owner reads it" 200 "$(as viewer "/Audio/$stub/hls/${job}7/stream.aac")"
plant jmfree.aac
check "an audio file of no recorded job goes to stock" 200 "$(as outsider "/Audio/$private/hls/jmfree/stream.aac")"

aacs_before=$(files | grep -c '\.aac$' || true)
progressive=$(open_local viewer)
as viewer "/Audio/$stub/stream.aac?audioCodec=aac&container=aac&playSessionId=$progressive&deviceId=jm-play-viewer" --max-time 4 >/dev/null || true
real=$(files | grep '\.aac$' | grep -v "^${job}7\.aac$" | grep -v '^jmfree' | head -1 || true)
if [ -n "$real" ]; then
  check "another user cannot read the progressive audio of a play session" "$none_audio" \
    "$(shape outsider "/Audio/$private/hls/${real%.aac}/stream.aac")"
  check "its owner reads it" 206 "$(as viewer "/Audio/$stub/hls/${real%.aac}/stream.aac" --max-time 4 -r 0-99 || true)"
else
  echo "info no progressive .aac was written for the play session ($aacs_before before): its audio refusal is not proved"
fi

# The legacy segment route answers a refused playlist id as one that matches no file, so growing a short id one hex
# digit at a time tells another user nothing of the name of the job.
paths=$(files | sed "s|^|$TRANSCODES/|")
two_in_job=""
two_nowhere=""
for a in 0 1 2 3 4 5 6 7 8 9 a b c d e f; do
  for b in 0 1 2 3 4 5 6 7 8 9 a b c d e f; do
    hits=$(grep -ci -- "$a$b" <<<"$paths" || true)
    [ -z "$two_nowhere" ] && [ "$hits" = 0 ] && two_nowhere=$a$b
    if [ -z "$two_in_job" ] && [[ $job == *"$a$b"* ]] && ! grep -v "/$job" <<<"$paths" | grep -qi -- "$a$b"; then two_in_job=$a$b; fi
  done
done
echo "info two digits only in the job: [$two_in_job], in no file: [$two_nowhere]"
if [ -n "$two_in_job" ] && [ -n "$two_nowhere" ]; then
  check "a two digit playlist id of the job answers as one of no file" "$(shape outsider "/Videos/$private/hls/$two_nowhere/none.ts")" \
    "$(shape outsider "/Videos/$private/hls/$two_in_job/none.ts")"
fi
check "a whole playlist id of the job answers as one of no file" "$(shape outsider "/Videos/$private/hls/$two_nowhere/none.ts")" \
  "$(shape outsider "/Videos/$private/hls/$job/none.ts")"
check "and so does a segment of the job" "$none_segment" "$(shape outsider "/Videos/$private/hls/$job/${job}0.ts")"
check "its owner reads that segment" 200 "$(as viewer "/Videos/$stub/hls/$job/${job}0.ts")"

as viewer "/Videos/ActiveEncodings?deviceId=jm-play-viewer&playSessionId=$psid" -X DELETE >/dev/null
as viewer "/Videos/ActiveEncodings?deviceId=jm-play-viewer&playSessionId=$progressive" -X DELETE >/dev/null

# Row 229: a /JellyMesh/Playback session opened while the stub sat in a library. The remote serves the file as it is,
# so the profile that plays it directly gets the proxy.
DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[]}}'
open_proxy() { # open_proxy <user>: opens the stub through the proxy, prints the path of its stream
  as "$1" "/Items/$stub/PlaybackInfo?userId=$(user_id local "$1")" -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -d "$DIRECT" >/dev/null
  jq -r '.MediaSources[0].TranscodingUrl // ""' "$WORK/as-body"
}
film_folder=$(dirname "$(dirname "$(strm_file local "$TITLE")")")
film_library=$(get local /Library/VirtualFolders | jq -r '.[] | select(.Name == "Film") | .ItemId')
refresh_film() { # refresh_film <user> <status>: refreshes the library Film alone, until the stub item answers so to the user
  post local "/Items/$film_library/Refresh?Recursive=false" "" >/dev/null
  local i
  for i in $(seq 1 30); do [ "$(as "$1" "/Users/$(user_id local "$1")/Items/$stub")" = "$2" ] && break; sleep 2; done
}
viewer_proxy=$(open_proxy viewer)
admin_proxy=$(open_proxy admin)
echo "info the proxied stream of viewer: ${viewer_proxy%%\?*}"
check "viewer plays the stub through the proxy" 1 "$(echo "$viewer_proxy" | grep -c '/JellyMesh/Playback/' || true)"
check "and so does the admin" 1 "$(echo "$admin_proxy" | grep -c '/JellyMesh/Playback/' || true)"
check "viewer's proxied stream is served" 206 "$(as viewer "$viewer_proxy" -H 'Range: bytes=0-99')"
check "the folder of the server leaves the library Film" 204 "$(unlink_folder local Film "$film_folder")"
refresh_film outsider 200
check "after a refresh of Film alone Jellyfin shows the stub to outsider" 200 "$(as outsider "/Users/$(user_id local outsider)/Items/$stub")"
check "viewer's open proxied stream stops" 404 "$(as viewer "$viewer_proxy" -H 'Range: bytes=0-99')"
check "the admin's goes on" 206 "$(as admin "$admin_proxy" -H 'Range: bytes=0-99')"
check "the folder of the server joins the library Film again" 204 "$(link_folder local Film "$film_folder")"
refresh_film outsider 404
check "back in the library, viewer's proxied stream is served again" 206 "$(as viewer "$viewer_proxy" -H 'Range: bytes=0-99')"

[ "${DOWN:-0}" = 1 ] && down
exit $FAILED
