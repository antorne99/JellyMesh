#!/usr/bin/env bash
# What a peer can reach, no cycles (A->B->A and stubs), and shared libraries enforced on the provider.
# Runs after federate.sh. usage: MEDIA=... WORK=... boundaries.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0

movies() { get "$1" '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path'; }
id_of() { movies "$1" | jq -r --arg p "$2" '[.Items[] | select(.Path | startswith($p))][0].Id'; }
admin_ids() { # <server> <path prefix> <type>...: IDs of the items under a path
  local s=$1 prefix=$2 type
  shift 2
  for type in "$@"; do
    get "$s" "/Items?Recursive=true&IncludeItemTypes=$type&Fields=Path" | jq -r --arg p "$prefix" '.Items[] | select((.Path // "") | startswith($p)) | .Id'
  done
}
provider_ids() { # <server> <token> <type>...: IDs of what a peer can read
  local s=$1 token=$2 library type
  shift 2
  as_peer "$s" "$token" GET /JellyMesh/Provider/Libraries >/dev/null
  local libraries
  libraries=$(jq -r '.Items[].Id' "$WORK/body")
  for library in $libraries; do
    for type in "$@"; do
      as_peer "$s" "$token" GET "/JellyMesh/Provider/Items?parentId=$library&includeItemTypes=$type&limit=500" >/dev/null
      jq -r '.Items[].Id' "$WORK/body"
    done
  done
}

token=$(peer_token a peer)
stub=$(id_of a /federata/)
real=$(id_of a /media/)
echo "stub on a: $stub, real item on a: $real"

echo "--- stock routes are closed to a peer"
for path in '/Items?Recursive=true' /UserViews /Users /Library/VirtualFolders /Sessions /System/Info /Shows/NextUp; do
  check "peer on $path" 403 "$(as_peer a "$token" GET "$path")"
done
check "peer on its own user" 200 "$(as_peer a "$token" GET /Users/Me)"

echo "--- stubs never reach a peer, by any route"
check "PlaybackInfo of a stub" 404 "$(as_peer a "$token" POST "/Items/$stub/PlaybackInfo")"
check "stream of a stub" 404 "$(as_peer a "$token" GET "/Videos/$stub/stream?static=true")"
check "image of a stub" 404 "$(as_peer a "$token" GET "/Items/$stub/Images/Primary/0")"
as_peer a "$token" GET "/JellyMesh/Provider/Items?ids=$stub&includeItemTypes=Movie" >/dev/null
check "lookup of a stub" 0 "$(jq '.Items | length' "$WORK/body")"
check "a real item stays reachable" 200 "$(as_peer a "$token" POST "/Items/$real/PlaybackInfo")"
as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/body")" >/dev/null

echo "--- no cycles"
# By ID, because a stub and a real copy can share a title. Series rows go one by one: a list collapses a split series.
for s in a b; do
  [ "$s" = a ] && t=$token || t=$(peer_token b peer)
  stubs=$(admin_ids "$s" /federata/ Movie Episode Season)
  real_count=$(admin_ids "$s" /media/ Movie Episode | wc -l | tr -d ' ')
  shared=$(provider_ids "$s" "$t" Movie Series Season Episode)
  shared_count=$(provider_ids "$s" "$t" Movie Episode | wc -l | tr -d ' ')
  [ -n "$stubs" ] && has_stubs=yes || has_stubs=no
  check "$s holds stubs in the shared libraries, so the test can fail" yes "$has_stubs"
  check "$s shares every real film and episode" "$real_count" "$shared_count"
  check "stubs of $s among what it shares" 0 "$(comm -12 <(sort <<<"$stubs") <(sort <<<"$shared") | grep -c .)"
  outside=0
  for id in $(provider_ids "$s" "$t" Series); do
    case "$(get "$s" "/Items/$id" | jq -r .Path)" in /media/*) ;; *) outside=$((outside + 1)) ;; esac
  done
  check "series rows of $s outside its real media" 0 "$outside"
done

as_peer a "$token" GET "/JellyMesh/Provider/Items?ids=$real&includeItemTypes=Movie" >/dev/null
check "a real item is found by a peer" 1 "$(jq '.Items | length' "$WORK/body")"
check "its versions come with quality" true "$(jq --arg m "$(jq -r '.Items[0].MediaSources[0].Id' "$WORK/body")" '.Quality[$m].Height > 0' "$WORK/body")"
check "no file path is sent to a peer" 0 "$(jq '[.Items[].MediaSources[] | (.Path // empty), (.MediaStreams[]? | .Path // empty)] | length' "$WORK/body")"

stubs_a=$(docker exec jm-prov-a sh -c 'find /federata -name "*.strm"')
stubs_b=$(docker exec jm-prov-b sh -c 'find /federata -name "*.strm"')
before_a=$(echo "$stubs_a" | wc -l | tr -d ' ')
before_b=$(echo "$stubs_b" | wc -l | tr -d ' ')
for round in 1 2 3; do
  sync_now a
  sync_now b
  scan a
  scan b
done
check "stubs on a stay after three rounds" "$before_a" "$(docker exec jm-prov-a sh -c 'find /federata -name "*.strm" | wc -l' | tr -d ' ')"
check "stubs on b stay after three rounds" "$before_b" "$(docker exec jm-prov-b sh -c 'find /federata -name "*.strm" | wc -l' | tr -d ' ')"

echo "--- others are not peers"
mario=$(peer_token a mario)
check "capabilities for an ordinary user" 404 "$(as_peer a "$mario" GET /JellyMesh/Capabilities)"
check "ProviderApi for an ordinary user" 403 "$(as_peer a "$mario" GET /JellyMesh/Provider/Libraries)"
check "an ordinary user keeps the stock API" 200 "$(as_peer a "$mario" GET /UserViews)"
check "capabilities without a login" 401 "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18410/JellyMesh/Capabilities)"
check "ProviderApi without a login" 401 "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18410/JellyMesh/Provider/Libraries)"

echo "--- shared libraries are enforced on the provider"
serie=$(library_id a Serie)
film=$(library_id a Film)
episode=$(get a '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path' | jq -r '[.Items[] | select(.Path | startswith("/media/"))][0].Id')
share a 1 Film
as_peer a "$token" GET /JellyMesh/Provider/Libraries >/dev/null
check "libraries offered" "$film" "$(jq -r '[.Items[].Id] | join(",")' "$WORK/body")"
check "asking for the library that is not shared" 404 "$(as_peer a "$token" GET "/JellyMesh/Provider/Items?parentId=$serie&includeItemTypes=Episode")"
check "PlaybackInfo outside the share" 404 "$(as_peer a "$token" POST "/Items/$episode/PlaybackInfo")"
check "stream outside the share" 404 "$(as_peer a "$token" GET "/Videos/$episode/stream?static=true")"
as_peer a "$token" GET "/JellyMesh/Provider/Items?ids=$episode&includeItemTypes=Episode" >/dev/null
check "lookup outside the share" 0 "$(jq '.Items | length' "$WORK/body")"

server=$(cat "$WORK/id-b-from-a")
sync_now b
check "the consumer learns the library is gone" LibraryUnavailable "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"
share a 1 Film Serie
sync_now b
check "and recovers when it is back" null "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"

exit "$FAILED"
