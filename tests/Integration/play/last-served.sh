#!/usr/bin/env bash
# Test 16 of the spec (D15): the last server that served a title, per user and stub, survives a restart of the local server.
# Two stock remotes with the same film, so the stub has a winner and a backup. Alice plays with the winner stopped and is
# served by the backup; Bob plays with both up and is served by the winner. After a restart each keeps their own server.
#   local  $PREFIX-local  $PORT      plugin, consumer of a and b
#   a, b   $PREFIX-a/-b   $PORT+1/2  stock Jellyfin with the media of remoto-standard
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-last] [PORT=18436] last-served.sh [--keep]
set -euo pipefail
PREFIX=${PREFIX:-jm-last}
PORT=${PORT:-18436}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

WORK=${WORK_LAST:-${TMPDIR:-/tmp}/$PREFIX-run}
mkdir -p "$WORK"
DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[]}}'

name_of() { case $1 in alice | bob) echo "$PREFIX-local" ;; *) echo "$PREFIX-$1" ;; esac; }
port_of() { case $1 in local | alice | bob) echo "$PORT" ;; a) echo $((PORT + 1)) ;; b) echo $((PORT + 2)) ;; esac; }

down_last() {
  docker rm -f "$PREFIX-local" "$PREFIX-a" "$PREFIX-b" >/dev/null 2>&1 || true
  docker volume rm "$PREFIX-local-config" "$PREFIX-local-cache" "$PREFIX-local-fed" "$PREFIX-local-media" \
    "$PREFIX-a-config" "$PREFIX-a-cache" "$PREFIX-a-fed" "$PREFIX-b-config" "$PREFIX-b-cache" "$PREFIX-b-fed" >/dev/null 2>&1 || true
}
[ "${1:-}" = --keep ] || trap down_last EXIT
down_last

for r in a b; do
  start "$r" "$MEDIA/remoto-standard:/media:ro"
  library "$r" Film movies /media/film
  user "$r" jellymesh >/dev/null
  scan "$r"
done

start local "$PREFIX-local-media:/media"
docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
library local Film movies /media/film /federata/Film
user local alice >/dev/null
user local bob >/dev/null
NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id_a=$(add_remote local a "http://host.docker.internal:$(port_of a)" jellymesh)
id_b=$(add_remote local b "http://host.docker.internal:$(port_of b)" jellymesh)
import_all local "$id_a"
import_all local "$id_b"
sync_now local
scan local

tears=$(stub_item local "Tears of Steel")
check "the film is a stub" 1 "$([ -n "$tears" ] && echo 1 || echo 0)"
winner=$(get local /JellyMesh/Admin/Index/Decisions | jq -r '[.Contents[] | select((.Backups // []) | length > 0) | select(.Identity.Key | test("tears|2285752|133701"; "i"))][0].Winner.ServerName // ""')
case $winner in a) backup=b ;; b) backup=a ;; *) echo "no winner with a backup: [$winner]" >&2; exit 1 ;; esac
id_of() { case $1 in a) echo "$id_a" ;; b) echo "$id_b" ;; esac; }
echo "winner $winner ($(id_of "$winner")), backup $backup ($(id_of "$backup"))"

play() { # play <user>: a PlaybackInfo of the stub as that user; prints the status
  login "$1" "$1"
  post "$1" "/Items/$tears/PlaybackInfo?UserId=$(user_id local "$1")&AutoOpenLiveStream=false" "$DIRECT" | jq -r '.MediaSources | length'
}
record() { # record <user>: the server recorded for that user on any stub
  docker exec "$PREFIX-local" sh -c 'cat "$(find /config -name last-served.json | head -1)"' \
    | jq -r --arg u "$(user_id local "$1" | tr -d -)" '[.Entries[] | select((.UserId | gsub("-"; "")) == $u) | .ServerId | gsub("-"; "")][0] // ""'
}
plain() { echo "$1" | tr -d - | tr '[:upper:]' '[:lower:]'; }

docker stop "$PREFIX-$winner" >/dev/null
check "alice is served with the winner stopped" 1 "$(play alice)"
docker start "$PREFIX-$winner" >/dev/null
wait_ready "$winner"
login local
check "bob is served with both up" 1 "$(play bob)"

login local
check "alice: the backup that served" "$(plain "$(id_of "$backup")")" "$(record alice)"
check "bob: the winner that served" "$(plain "$(id_of "$winner")")" "$(record bob)"

docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "after a restart alice still has the backup" "$(plain "$(id_of "$backup")")" "$(record alice)"
check "after a restart bob still has the winner" "$(plain "$(id_of "$winner")")" "$(record bob)"

file=$(docker exec "$PREFIX-local" sh -c 'cat "$(find /config -name last-served.json | head -1)"')
check "no address in the record" 0 "$(echo "$file" | grep -ciE 'http|host\.docker|:[0-9]{4,5}' || true)"
check "no token in the record" 0 "$(echo "$file" | grep -ciE 'token|api_?key|sig=' || true)"
check "no remote item in the record" 0 "$(echo "$file" | grep -c "MediaSourceId\|ItemId" || true)"
echo "$file"
exit "$FAILED"
