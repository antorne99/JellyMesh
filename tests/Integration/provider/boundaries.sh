#!/usr/bin/env bash
# What a peer can reach, no cycles (A->B->A and stubs), and shared libraries enforced on the provider.
# Runs after federate.sh. usage: MEDIA=... WORK=... boundaries.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0

FAILED_READS=$WORK/failed-reads
: >"$FAILED_READS"

some() { [ -n "$1" ] && echo yes || echo no; }
admin_list() { # <server> <path> <jq argument>...: reads a list as the admin, and notes a read that fails
  local s=$1 path=$2
  shift 2
  get "$s" "$path" >/dev/null
  { [ "$(status)" = 200 ] && jq "$@" "$WORK/body"; } || echo "$s, $path: $(status)" >>"$FAILED_READS"
}
peer_list() { # <server> <token> <path> <jq filter>: the same as a peer
  local code
  code=$(as_peer "$1" "$2" GET "$3")
  { [ "$code" = 200 ] && jq -r "$4" "$WORK/body"; } || echo "$1 as a peer, $3: $code" >>"$FAILED_READS"
}
movies() { get "$1" '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path'; }
id_of() { movies "$1" | jq -r --arg p "$2" '[.Items[] | select(.Path | startswith($p))][0].Id'; }
admin_ids() { # <server> <path prefix> <type>...: IDs of the items under a path
  local s=$1 prefix=$2 type
  shift 2
  for type in "$@"; do
    admin_list "$s" "/Items?Recursive=true&IncludeItemTypes=$type&Fields=Path" -r --arg p "$prefix" '.Items[] | select((.Path // "") | startswith($p)) | .Id'
  done
}
shared_stubs() { # <server> <token> <type>: IDs of the stubs Jellyfin has in the libraries a peer is offered
  local s=$1 library
  for library in $(peer_list "$s" "$2" /JellyMesh/Provider/Libraries '.Items[].Id'); do
    admin_list "$s" "/Items?ParentId=$library&Recursive=true&IncludeItemTypes=$3&Fields=Path" -r '.Items[] | select((.Path // "") | startswith("/federata/")) | .Id'
  done
}
titles_of() { # <server>: every film and episode, one per line, with the IDs of its file versions and of its stub versions
  admin_list "$1" '/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Fields=Path,MediaSources' -c \
    '.Items[] | {Id, Type} + (.MediaSources // [] | map({Id, File: ((.Path // "") | startswith("/media/"))}) |
      {Files: map(select(.File).Id), Stubs: map(select(.File | not).Id)})'
}
ask_version() { # <server> <token> <item> [version] [method]: the status of PlaybackInfo, the versions in $WORK/asked, and its slot freed
  local code session
  code=$(as_peer "$1" "$2" "${5:-POST}" "/Items/$3/PlaybackInfo${4:+?mediaSourceId=$4}")
  jq -r '.MediaSources[]?.Id' "$WORK/body" 2>/dev/null | sort -u >"$WORK/asked"
  session=$(jq -r '.PlaySessionId // empty' "$WORK/body" 2>/dev/null)
  [ -z "$session" ] || as_peer "$1" "$2" DELETE "/JellyMesh/Provider/Streams/$session" >/dev/null
  echo "$code"
}
provider_ids() { local s=$1 token=$2; shift 2; provider_read "$s" "$token" '.Items[].Id' "$@"; }
provider_read() { # <server> <token> <jq filter> <type>...: what a peer can read
  local s=$1 token=$2 filter=$3 library type
  shift 3
  for library in $(peer_list "$s" "$token" /JellyMesh/Provider/Libraries '.Items[].Id'); do
    for type in "$@"; do
      peer_list "$s" "$token" "/JellyMesh/Provider/Items?parentId=$library&includeItemTypes=$type&limit=500" "$filter"
    done
  done
}

token=$(peer_token a peer)
stub=$(shared_stubs a "$token" Movie)
stub=${stub%%$'\n'*}
real=$(id_of a /media/)
echo "stub on a: $stub, real item on a: $real"
check "a has a stub to ask for" yes "$([[ $stub =~ ^[0-9a-f]{32}$ ]] && echo yes || echo no)"
check "a has a real film to ask for" yes "$([[ $real =~ ^[0-9a-f]{32}$ ]] && echo yes || echo no)"

echo "--- stock routes are closed to a peer"
for path in '/Items?Recursive=true' /UserViews /Users /Library/VirtualFolders /Sessions /System/Info /Shows/NextUp; do
  check "peer on $path" 403 "$(as_peer a "$token" GET "$path")"
done
check "peer on its own user" 200 "$(as_peer a "$token" GET /Users/Me)"

echo "--- stubs never reach a peer, by any route"
check "PlaybackInfo of a stub" 404 "$(as_peer a "$token" POST "/Items/$stub/PlaybackInfo")"
check "stream of a stub" 404 "$(as_peer a "$token" GET "/Videos/$stub/stream?static=true")"
check "image of a stub" 404 "$(as_peer a "$token" GET "/Items/$stub/Images/Primary/0")"
check "segments of a stub" 404 "$(as_peer a "$token" GET "/MediaSegments/$stub")"
check "lookup of a stub" 0 "$(peer_list a "$token" "/JellyMesh/Provider/Items?ids=$stub&includeItemTypes=Movie" '.Items | length')"
check "a real item stays reachable" 200 "$(as_peer a "$token" POST "/Items/$real/PlaybackInfo")"
as_peer a "$token" DELETE "/JellyMesh/Provider/Streams/$(jq -r .PlaySessionId "$WORK/body")" >/dev/null
check "segments of a real item stay reachable" 200 "$(as_peer a "$token" GET "/MediaSegments/$real")"

echo "--- no cycles"
# By ID, because a stub and a real copy can share a title. Series rows go one by one: a list collapses a split series.
for s in a b; do
  [ "$s" = a ] && t=$token || t=$(peer_token b peer)
  stubs=$(admin_ids "$s" /federata/ Movie Episode Season)
  real_count=$(admin_ids "$s" /media/ Movie Episode | wc -l | tr -d ' ')
  shared=$(provider_ids "$s" "$t" Movie Series Season Episode)
  for type in Movie Episode Season; do
    offered=$(shared_stubs "$s" "$t" "$type")
    echo "$s: $(grep -c . <<<"$offered") stubs of type $type in the shared libraries"
    check "$s holds $type stubs in the shared libraries, so the test can fail" yes "$(some "$offered")"
    stubs+=$'\n'$offered
  done
  for type in Movie Episode; do
    check "$s has real items of type $type" yes "$(some "$(admin_ids "$s" /media/ "$type")")"
  done
  shared_count=$(provider_ids "$s" "$t" Movie Episode | wc -l | tr -d ' ')
  check "$s shares every real film and episode" "$real_count" "$shared_count"
  check "stubs of $s among what it shares" 0 "$(comm -12 <(sort -u <<<"$stubs") <(sort -u <<<"$shared") | grep -c .)"
  outside=0
  series=$(provider_ids "$s" "$t" Series)
  check "$s shares series rows, so the test can fail" yes "$(some "$series")"
  check "$s shares season rows, so the test can fail" yes "$(some "$(provider_ids "$s" "$t" Season)")"
  for id in $series; do
    case "$(get "$s" "/Items/$id" | jq -r .Path)" in /media/*) ;; *) outside=$((outside + 1)) ;; esac
  done
  check "series rows of $s outside its real media" 0 "$outside"

  # A stub linked to a real copy is a version of that copy.
  titles=$(titles_of "$s")
  files=$(jq -r '.Files[]' <<<"$titles" | sort -u)
  linked=$(jq -r '.Stubs[]' <<<"$titles" | sort -u)
  pairs=$(jq -r '.Id as $item | (.Files | if index($item) then $item else .[0] // empty end) as $real | .Type as $type | .Stubs[] | "\($real) \(.) \($type)"' <<<"$titles")
  versions=$(provider_read "$s" "$t" '.Items[].MediaSources[]?.Id' Movie Episode | sort -u)
  echo "$s: $(grep -c . <<<"$pairs") stub versions linked to real items, $(grep -c . <<<"$versions") versions sent to the peer"
  for type in Movie Episode; do
    check "$s has stub versions linked to real items of type $type, so the test can fail" yes "$(some "$(grep " $type$" <<<"$pairs")")"
  done
  check "$s has file versions" yes "$(some "$files")"
  check "file versions of $s a peer does not receive" 0 "$(comm -23 <(echo "$files") <(echo "$versions") | grep -c .)"
  check "stub versions of $s among those a peer receives" 0 "$(comm -12 <(echo "$linked") <(echo "$versions") | grep -c .)"
  by_file='' other=''
  while read -r item version; do
    [ -n "$version" ] || continue
    [ "$version" = "$item" ] || other+="$version "
    by_file+="$(ask_version "$s" "$t" "$item" "$version"),$([ "$(cat "$WORK/asked")" = "$version" ] && echo alone || echo not-alone) "
  done < <(jq -r '.Id as $item | select(.Stubs != [] or (.Files | length > 1)) | .Files | (if index($item) then $item else .[0] // empty end) as $real | .[] | "\($real) \(.)"' <<<"$titles")
  check "PlaybackInfo of a file version of $s, with that version and no other in the answer" 200,alone "$(tr ' ' '\n' <<<"$by_file" | grep . | sort -u | paste -sd' ' -)"
  [ "$s" = b ] || check "$s has a file version that is not the item asked, so the test can fail" yes "$(some "$other")"
  for method in POST GET; do
    unnamed='' outside=''
    for item in $(cut -d' ' -f1 <<<"$pairs" | sort -u); do
      own=$(jq -r --arg id "$item" 'select(.Files | index($id)) | .Files[]' <<<"$titles" | sort -u)
      unnamed+="$(ask_version "$s" "$t" "$item" '' "$method"),$(some "$(comm -12 <(echo "$own") "$WORK/asked" | grep .)") "
      outside+=$(comm -13 <(echo "$own") "$WORK/asked")$'\n'
    done
    check "$method PlaybackInfo of a title of $s with no version named, with a file version in the answer" 200,yes "$(tr ' ' '\n' <<<"$unnamed" | grep . | sort -u | paste -sd' ' -)"
    check "versions in $method PlaybackInfo of $s with no version named that are not files of the title" 0 "$(grep -c . <<<"$outside")"
  done
  by_item='' by_version=''
  while read -r item version type; do
    [ -n "$version" ] || continue
    by_item+="$(ask_version "$s" "$t" "$item" "$version") "
    by_version+="$(ask_version "$s" "$t" "$version") "
  done <<<"$pairs"
  check "PlaybackInfo of a stub version of $s, by its real item" 404 "$(tr ' ' '\n' <<<"$by_item" | grep . | sort -u | paste -sd' ' -)"
  check "PlaybackInfo of a stub version of $s, by its own ID" 404 "$(tr ' ' '\n' <<<"$by_version" | grep . | sort -u | paste -sd' ' -)"
done

as_peer a "$token" GET "/JellyMesh/Provider/Items?ids=$real&includeItemTypes=Movie" >/dev/null
check "a real item is found by a peer" 1 "$(jq '.Items | length' "$WORK/body")"
check "its versions come with quality" true "$(jq --arg m "$(jq -r '.Items[0].MediaSources[0].Id' "$WORK/body")" '.Quality[$m].Height > 0' "$WORK/body")"
check "no file path is sent to a peer" 0 "$(jq '[.Items[].MediaSources[] | (.Path // empty), (.MediaStreams[]? | .Path // empty)] | length' "$WORK/body")"

stubs_a=$(docker exec "$PREFIX-a" sh -c 'find /federata -name "*.strm"')
stubs_b=$(docker exec "$PREFIX-b" sh -c 'find /federata -name "*.strm"')
before_a=$(echo "$stubs_a" | wc -l | tr -d ' ')
before_b=$(echo "$stubs_b" | wc -l | tr -d ' ')
for round in 1 2 3; do
  sync_now a
  sync_now b
  scan a
  scan b
done
check "stubs on a stay after three rounds" "$before_a" "$(docker exec "$PREFIX-a" sh -c 'find /federata -name "*.strm" | wc -l' | tr -d ' ')"
check "stubs on b stay after three rounds" "$before_b" "$(docker exec "$PREFIX-b" sh -c 'find /federata -name "*.strm" | wc -l' | tr -d ' ')"

echo "--- others are not peers"
mario=$(peer_token a mario)
check "capabilities for an ordinary user" 404 "$(as_peer a "$mario" GET /JellyMesh/Capabilities)"
check "ProviderApi for an ordinary user" 403 "$(as_peer a "$mario" GET /JellyMesh/Provider/Libraries)"
check "an ordinary user keeps the stock API" 200 "$(as_peer a "$mario" GET /UserViews)"
check "capabilities without a login" 401 "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT_A/JellyMesh/Capabilities")"
check "ProviderApi without a login" 401 "$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT_A/JellyMesh/Provider/Libraries")"

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
check "segments outside the share" 404 "$(as_peer a "$token" GET "/MediaSegments/$episode")"
check "lookup outside the share" 0 "$(peer_list a "$token" "/JellyMesh/Provider/Items?ids=$episode&includeItemTypes=Episode" '.Items | length')"

server=$(cat "$WORK/id-b-from-a")
sync_now b
check "the consumer learns the library is gone" LibraryUnavailable "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"
share a 1 Film Serie
sync_now b
check "and recovers when it is back" null "$(get b /JellyMesh/Admin/Sync | jq -r --arg i "$server" '.Servers[] | select(.ServerId == $i) | .Failure')"

cat "$FAILED_READS"
check "lists that could not be read" 0 "$(grep -c . "$FAILED_READS")"
exit "$FAILED"
