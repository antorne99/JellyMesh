#!/usr/bin/env bash
# Integration test 6 of the per-server spec, for the fallback per user (F11): with remote stopped, a user who sees only
# remote is not carried to peer and the .strm of remote stays on remote; a user who sees both falls back to the stub of
# peer and does not move the first. The folder of peer is in a library of its own. Needs up.sh, and no other test before.
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
only() { # only <user> <library>...: the user sees those libraries alone
  local user=$1 libs uid policy
  shift
  libs=$(get local /Library/VirtualFolders | jq -c --args '[.[] | select(.Name | IN($ARGS.positional[])) | .ItemId]' "$@")
  uid=$(user_id local "$user")
  policy=$(get local "/Users/$uid" | jq -c --argjson f "$libs" '.Policy | .EnableAllFolders = false | .EnabledFolders = $f')
  post local "/Users/$uid/Policy" "$policy" >/dev/null
}
ids_of() { jq -r '[(.MediaSources // [])[].Id] | join(",")'; }
leaks() { # leaks <user> <response>: what of a remote it carries, an address or a token other than the user's own
  { grep -Eo "host\.docker\.internal|:$((PORT + 1))|:$((PORT + 2))" <<<"$2"
    grep -Eio "(api_?key|token)=[^&\"]*" <<<"$2" | grep -vF "=$(cat "$WORK/tok-$1")"; } | sort -u | paste -sd, - || true
}
# strm <url>: the status of a read of a .strm, from inside the local server as its ffmpeg reads it.
strm() { docker exec "$PREFIX-local" curl -s -o /dev/null -m 60 -w '%{http_code}' -H 'Range: bytes=0-99' "$1"; }

echo "--- setup: the folder of peer in the library Film Peer, a user who sees only remote and one who sees both"
folder_b=$(server_folder "$B")
check "the Film folder of peer leaves Film" 204 "$(unlink_folder local Film "$folder_b/Film")"
post local "/Library/VirtualFolders?name=Film%20Peer&collectionType=movies&refreshLibrary=false&paths=$folder_b/Film" \
  '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
scan local
sync_now local
for u in uno due; do user local "$u" >/dev/null; done
only uno Film
only due Film "Film Peer"
sa=$(stub_of "$A" "$RES"); sb=$(stub_of "$B" "$RES")
check "both stubs are in the libraries" 1 "$([ -n "$sa" ] && [ -n "$sb" ] && echo 1 || echo 0)"
for i in $(seq 1 10); do
  [ "$(primary_of "$sa")" = "$sb" ] && break
  sync_now local
done
check "the stubs are linked as versions" "$sb" "$(primary_of "$sa")"
url_a=$(strm_text local "$RES" "$(server_folder "$A")/Film")
check "the .strm of remote opens with remote up" 206 "$(strm "$url_a")"

r=$(as_user uno POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
check "with remote up, who sees only remote gets its stub" "200 $sa" "$(status) $(ids_of <<<"$r")"

echo "--- test 6: remote stopped"
docker stop "$PREFIX-remote" >/dev/null
r=$(as_user uno POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
echo "    who sees only remote: $(status) $(jq -c '{ErrorCode, Ids: [(.MediaSources // [])[].Id]}' <<<"$r" 2>/dev/null || echo "$r")"
check "who sees only remote is not carried to peer" "" "$(ids_of <<<"$r" | grep -F "$sb" || true)"
check "nor served anything" "" "$(ids_of <<<"$r")"
check "the .strm of remote does not move to peer" 503 "$(strm "$url_a")"

r=$(as_user due POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$sa" "$DIRECT")
check "who sees both falls back from the stub of remote to the stub of peer" "200 $sb" "$(status) $(ids_of <<<"$r")"
check "with nothing of the remote in it" "" "$(leaks due "$r")"
session=$(jq -r .PlaySessionId <<<"$r")
r=$(as_user due POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$sa" "$DIRECT")
check "asked again, the same fallback" "200 $sb" "$(status) $(ids_of <<<"$r")"
check "on a new play session" 1 "$([ "$(jq -r .PlaySessionId <<<"$r")" != "$session" ] && echo 1 || echo 0)"

r=$(as_user uno POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
check "the fallback of the second user does not move the first" "" "$(ids_of <<<"$r")"
check "the .strm of remote still does not move" 503 "$(strm "$url_a")"
r=$(as_user uno POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$sb" "$DIRECT")
check "who sees only remote cannot name the stub of peer" 404 "$(status)"

echo "--- remote back"
docker start "$PREFIX-remote" >/dev/null
wait_ready remote
for i in $(seq 1 60); do
  r=$(as_user uno POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
  [ "$(status) $(ids_of <<<"$r")" = "200 $sa" ] && break
  sleep 2
done
check "with remote back, who sees only remote gets its stub again" "200 $sa" "$(status) $(ids_of <<<"$r")"
check "and its .strm opens" 206 "$(strm "$url_a")"

exit "$FAILED"
