#!/usr/bin/env bash
# Reconnection during playback (row 57): a remote stopped during playback gets a quick 5xx, the next PlaybackInfo goes to
# the other remote, and the fourth reconnection within five minutes gets a 503 while another user still plays. Each drop
# restarts the stopped remote. Needs up.sh, and no other test before.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
A=$(cat "$WORK/id-remote")
B=$(cat "$WORK/id-peer")
RES="JM Confronto Risoluzione"   # 720p on remote, 1080p on peer

# as_user <user> <method> <path> [body]: prints the body as that user, on a device of its own; the status is in $WORK/status.
as_user() {
  local who=$1 method=$2 path=$3 body=${4:-} auth
  auth="MediaBrowser Client=\"JellyMeshPlay\", Device=\"$who\", DeviceId=\"$PREFIX-$who\", Version=\"0.1\""
  [ -s "$WORK/tok-$who" ] || curl -sS -X POST -H "$ACCEPT" -H 'Content-Type: application/json' -H "Authorization: $auth" \
    -d "{\"Username\":\"$who\",\"Pw\":\"jellymesh-test\"}" "http://127.0.0.1:$PORT/Users/AuthenticateByName" | jq -r .AccessToken >"$WORK/tok-$who"
  curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" -H "$ACCEPT" -H 'Content-Type: application/json' \
    -H "Authorization: $auth, Token=\"$(cat "$WORK/tok-$who")\"" ${body:+-d "$body"} "http://127.0.0.1:$PORT$path" >"$WORK/status"
  cat "$WORK/body"
}
now() { perl -MTime::HiRes=time -e 'printf "%.2f\n", time'; }
since() { perl -e "printf '%.1f', $(now) - $1"; }
served() { jq -r '(.MediaSources // [])[0].Id // ""'; }
stream_url() { jq -r '(.MediaSources // [])[0].TranscodingUrl // ""'; }
server_of() { [ "$1" = "$sa" ] && echo remote || echo peer; }
play() { as_user "${1:-tre}" POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT"; }
restart() { docker start "$PREFIX-$1" >/dev/null; wait_ready "$1"; }

# drop_while_reading <answer>: reads the stream slowly, stops its server in the middle, and tries the stream again.
drop_while_reading() {
  local url server t
  url="http://127.0.0.1:$PORT$(stream_url <<<"$1")"
  server=$(server_of "$(served <<<"$1")")
  curl -sS -o /dev/null --limit-rate 200k -m 120 -w '%{http_code} %{size_download}' "$url" >"$WORK/read" 2>/dev/null &
  local reader=$!
  sleep 3
  t=$(now)
  docker stop -t 2 "$PREFIX-$server" >/dev/null
  wait "$reader" || true
  echo "    the read of $server ended $(since "$t") s after the stop began: $(cat "$WORK/read")"
  t=$(now)
  check "the retry of the stream of $server gets a bad gateway" 502 \
    "$(curl -s -o /dev/null -m 60 -w '%{http_code}' -H 'Range: bytes=1000000-' "$url")"
  echo "    in $(since "$t") s"
  restart "$server"
}

# drop_between_reads <answer>: stops the server of the stream, then reads it.
drop_between_reads() {
  local url server t
  url="http://127.0.0.1:$PORT$(stream_url <<<"$1")"
  server=$(server_of "$(served <<<"$1")")
  check "the stream of $server plays" 206 "$(curl -s -o /dev/null -m 60 -w '%{http_code}' -H 'Range: bytes=0-99' "$url")"
  docker stop -t 2 "$PREFIX-$server" >/dev/null
  t=$(now)
  check "the stream of $server stopped gets a bad gateway" 502 \
    "$(curl -s -o /dev/null -m 60 -w '%{http_code}' -H 'Range: bytes=100-199' "$url")"
  echo "    in $(since "$t") s"
  restart "$server"
}

echo "--- setup: a user who sees both versions"
user local tre >/dev/null
user local quattro >/dev/null
sa=$(stub_of "$A" "$RES"); sb=$(stub_of "$B" "$RES")
check "both stubs are in the libraries" 1 "$([ -n "$sa" ] && [ -n "$sb" ] && echo 1 || echo 0)"
for i in $(seq 1 10); do
  [ "$(primary_of "$sa")" = "$sb" ] && break
  sync_now local
done
check "the stubs are linked as versions" "$sb" "$(primary_of "$sa")"

echo "--- drop 1: peer stops in the middle of a read"
r=$(play)
check "the best version starts, on peer" "200 $sb" "$(status) $(served <<<"$r")"
drop_while_reading "$r"

echo "--- reconnection 1"
r=$(play)
check "the next open goes to the other remote, though peer is back" "200 $sa" "$(status) $(served <<<"$r")"

echo "--- drop 2: remote stops between two reads"
drop_between_reads "$r"

echo "--- reconnection 2"
r=$(play)
check "with both dropped, the order of quality again" "200 $sb" "$(status) $(served <<<"$r")"

echo "--- drop 3: peer stops in the middle of a read"
drop_while_reading "$r"

echo "--- reconnection 3"
r=$(play)
check "the third reconnection still plays, in the order of quality" "200 $sb" "$(status) $(served <<<"$r")"

echo "--- drop 4"
drop_between_reads "$r"

echo "--- reconnection 4"
r=$(play)
check "the fourth reconnection gets a 503" 503 "$(status)"
r=$(play quattro)
check "another user still plays the title" 200 "$(status)"

exit "$FAILED"
