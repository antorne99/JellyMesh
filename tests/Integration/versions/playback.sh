#!/usr/bin/env bash
# Integration tests 3 and 15 of the per-server spec, for PlaybackInfo per version (F10): the request shapes of the web,
# Android TV and Swiftfin clients, the id of the version served, the version that starts for each user, and the position
# kept on that version. The local copy is in its own library, and each server folder in its own. Needs up.sh. Run once.
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
body_with() { echo "$DIRECT" | jq -c --arg m "$1" '. + {MediaSourceId: $m}'; }
ids_of() { jq -r '[.MediaSources[].Id] | join(",")'; }
# leaks <user> <response>: what of a remote it carries, an address or a token other than the user's own.
leaks() {
  { grep -Eo "host\.docker\.internal|:$((PORT + 1))|:$((PORT + 2))" <<<"$2"
    grep -Eio "(api_?key|token)=[^&\"]*" <<<"$2" | grep -vF "=$(cat "$WORK/tok-$1")"; } | sort -u | paste -sd, - || true
}
# position <user> <item>: the position the user has on an item.
position() { as_user "$1" GET "/Users/$(user_id local "$1")/Items/$(dashed "$2")" | jq -r '.UserData.PlaybackPositionTicks'; }

echo "--- setup: the local copy in the library Privato, the folder of peer in the library Film Peer"
docker exec "$PREFIX-local" mkdir -p "/media/privato/$RES (2026)"
docker cp "$MEDIA/remoto-jellymesh/film/$RES (2026)/." "$PREFIX-local:/media/privato/$RES (2026)/"
docker exec "$PREFIX-local" sh -c 'chmod -R a+rwX /media/privato'
post local "/Library/VirtualFolders?name=Privato&collectionType=movies&refreshLibrary=false&paths=/media/privato" \
  '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
folder_b=$(server_folder "$B")
check "the Film folder of peer leaves Film" 204 "$(unlink_folder local Film "$folder_b/Film")"
post local "/Library/VirtualFolders?name=Film%20Peer&collectionType=movies&refreshLibrary=false&paths=$folder_b/Film" \
  '{"LibraryOptions":{"EnableInternetProviders":false}}' >/dev/null
# The test films last a minute: stock keeps no position on anything shorter than its minimum.
post local /System/Configuration "$(get local /System/Configuration | jq -c '.MinResumeDurationSeconds = 0')" >/dev/null
scan local
sync_now local
for u in tutti senza-locale solo-remote; do user local "$u" >/dev/null; done
only senza-locale Film "Film Peer"
only solo-remote Film

loc=$(vid "/media/privato/$RES"); sa=$(stub_of "$A" "$RES"); sb=$(stub_of "$B" "$RES")
check "the local copy and both stubs are in the libraries" 1 "$([ -n "$loc" ] && [ -n "$sa" ] && [ -n "$sb" ] && echo 1 || echo 0)"
for i in $(seq 1 10); do
  [ "$(primary_of "$sa")" = "$loc" ] && [ "$(primary_of "$sb")" = "$loc" ] && break
  sync_now local
done
check "the local copy is the primary of the stub of remote" "$loc" "$(primary_of "$sa")"
check "and of the stub of peer" "$loc" "$(primary_of "$sb")"
film_links "$RES" | sed 's/^/    /'

echo "--- test 3, no version (web card, autoplay): the version that starts"
r=$(as_user tutti POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
check "who sees the local copy gets it under the stub of remote" "200 $loc" "$(status) $(ids_of <<<"$r")"
r=$(as_user tutti POST "/Items/$(dashed "$loc")/PlaybackInfo" "$DIRECT")
check "and under the local copy, with no stub listed" "200 $loc" "$(status) $(ids_of <<<"$r")"
r=$(as_user senza-locale POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
check "who does not see it gets the 1080p stub of peer" "200 $sb" "$(status) $(ids_of <<<"$r")"
check "with nothing of the remote in it" "" "$(leaks senza-locale "$r")"
r=$(as_user solo-remote POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
check "who sees only remote gets its 720p stub" "200 $sa" "$(status) $(ids_of <<<"$r")"
r=$(as_user senza-locale GET "/Items/$(dashed "$sa")/PlaybackInfo?userId=$(user_id local senza-locale)")
check "GET answers the same version" "200 $sb" "$(status) $(ids_of <<<"$r")"

echo "--- test 3, a named version"
r=$(as_user tutti POST "/Items/$(dashed "$loc")/PlaybackInfo" "$(body_with "$sa")")
check "web detail page: the version in the body wins over the local copy" "200 $sa" "$(status) $(ids_of <<<"$r")"
check "with nothing of the remote in it" "" "$(leaks tutti "$r")"
r=$(as_user tutti POST "/Items/$(dashed "$sb")/PlaybackInfo?mediaSourceId=$sb" "$DIRECT")
check "Android TV: the version as route and as source" "200 $sb" "$(status) $(ids_of <<<"$r")"
r=$(as_user tutti POST "/Items/$(dashed "$loc")/PlaybackInfo?mediaSourceId=$sb" "$(body_with "$sa")")
check "Swiftfin: the version in the query wins over the body" "200 $sb" "$(status) $(ids_of <<<"$r")"
r=$(as_user tutti POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$loc" "$DIRECT")
check "the local copy named under a stub is stock's" "200 $loc" "$(status) $(ids_of <<<"$r")"

echo "--- test 3, versions the user cannot see"
as_user solo-remote POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$sb" "$DIRECT" >/dev/null
check "the stub of peer is not found for who sees only remote" 404 "$(status)"
as_user solo-remote POST "/Items/$(dashed "$sa")/PlaybackInfo" "$(body_with "$loc")" >/dev/null
check "nor the local copy" 404 "$(status)"
other=$(stub_of "$A" "JM Confronto Bitrate")
as_user tutti POST "/Items/$(dashed "$sa")/PlaybackInfo?mediaSourceId=$other" "$DIRECT" >/dev/null
check "a version of another title is not found" 404 "$(status)"

echo "--- test 15: the position stays on the version served"
uid=$(user_id local senza-locale)
r=$(as_user senza-locale POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
served=$(ids_of <<<"$r"); session=$(jq -r .PlaySessionId <<<"$r")
check "the version served is the stub of peer" "$sb" "$served"
start=$(jq -nc --arg i "$(dashed "$sa")" --arg m "$served" --arg p "$session" '{ItemId: $i, MediaSourceId: $m, PlaySessionId: $p, PositionTicks: 0, PlayMethod: "DirectStream"}')
as_user senza-locale POST /Sessions/Playing "$start" >/dev/null
check "the start is taken" 204 "$(status)"
stop=$(jq -c '.PositionTicks = 420000000' <<<"$start")
as_user senza-locale POST /Sessions/Playing/Stopped "$stop" >/dev/null
check "the stop is taken" 204 "$(status)"
check "the position is on the stub of peer" 420000000 "$(position senza-locale "$sb")"
check "not on the stub of the route" 0 "$(position senza-locale "$sa")"
check "the user played nothing else" 0 "$(sql "select count(*) from UserData where lower(replace(UserId, '-', '')) = '$uid' and PlaybackPositionTicks > 0 and lower(replace(ItemId, '-', '')) != '$sb';")"
as_user senza-locale POST "/Sessions/Playing/Stopped" "$(jq -c '.PositionTicks = 0' <<<"$stop")" >/dev/null

uid=$(user_id local tutti)
r=$(as_user tutti POST "/Items/$(dashed "$sa")/PlaybackInfo" "$DIRECT")
start=$(jq -nc --arg i "$(dashed "$sa")" --arg m "$(ids_of <<<"$r")" --arg p "$(jq -r .PlaySessionId <<<"$r")" '{ItemId: $i, MediaSourceId: $m, PlaySessionId: $p, PositionTicks: 0, PlayMethod: "Transcode"}')
as_user tutti POST /Sessions/Playing "$start" >/dev/null
as_user tutti POST /Sessions/Playing/Stopped "$(jq -c '.PositionTicks = 310000000' <<<"$start")" >/dev/null
check "the local copy played under a stub keeps its own position" 310000000 "$(position tutti "$loc")"
check "and the stub keeps none" 0 "$(position tutti "$sa")"

echo "--- test 3: the other file of a local film with a stub"
MULTI="JM Versioni Multiple"
put_film "remoto-standard/film/$MULTI (2026)" "$MULTI (2026)"
scan local
sync_now local
m1=$(vid "/media/film/$MULTI (2026)/$MULTI (2026) - 1080p"); m2=$(vid "/media/film/$MULTI (2026)/$MULTI (2026) - 720p")
ms=$(stub_of "$A" "$MULTI")
check "the stub is linked to the local film" "$m1" "$(primary_of "$ms")"
r=$(as_user tutti POST "/Items/$(dashed "$m1")/PlaybackInfo" "$(body_with "$m2")")
check "the second file named under the first" "200 $m2" "$(status) $(ids_of <<<"$r")"
r=$(as_user tutti POST "/Items/$(dashed "$ms")/PlaybackInfo?mediaSourceId=$m2" "$DIRECT")
check "the second file named under the stub" "200 $m2" "$(status) $(ids_of <<<"$r")"
r=$(as_user tutti POST "/Items/$(dashed "$m2")/PlaybackInfo?mediaSourceId=$ms" "$DIRECT")
check "the stub named under the second file" "200 $ms" "$(status) $(ids_of <<<"$r")"

exit "$FAILED"
