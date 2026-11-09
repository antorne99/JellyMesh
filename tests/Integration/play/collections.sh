#!/usr/bin/env bash
# Task 845: a stock remote with "Group movies into collections" on answers /Items with the collection in place of
# its films. Proves that the sync query still reads both films of a collection and the consumer materializes them.
# usage: MEDIA=<main checkout>/tests/Integration/media collections.sh   (KEEP=1 leaves the containers up)
set -euo pipefail
WORK=${WORK:-${TMPDIR:-/tmp}/jm-col-run}
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

# Own containers and ports. rjm is the dedicated user on the remote: its requests go to remote with its own token.
port_of() { case $1 in local) echo 18480 ;; remote | rjm) echo 18481 ;; esac; }
name_of() { echo "jm-col-$1"; }
down() {
  docker rm -f jm-col-local jm-col-remote >/dev/null 2>&1 || true
  docker volume rm jm-col-local-config jm-col-local-cache jm-col-local-fed \
    jm-col-remote-config jm-col-remote-cache jm-col-remote-fed jm-col-local-media >/dev/null 2>&1 || true
}
[ "${KEEP:-}" = 1 ] || trap down EXIT

FILM_A="Tears of Steel"
FILM_B="Titolo Omonimo"

down
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
user remote jellymesh >/dev/null
scan remote
login rjm jellymesh

# film_id <title>: the remote film whose folder has that title, read before grouping can hide it.
film_id() { get remote '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg t "$1" '[.Items[] | select((.Path // "") | contains("/" + $t + " ("))][0].Id // ""'; }
a=$(film_id "$FILM_A")
b=$(film_id "$FILM_B")
check "both films are on the remote" true "$([ -n "$a" ] && [ -n "$b" ] && echo true || echo false)"

echo "--- the remote groups films into collections"
config=$(get remote /System/Configuration | jq -c '.EnableGroupingMoviesIntoCollections = true')
post remote /System/Configuration "$config" >/dev/null
check "the grouping option is saved" 204 "$(status)"
check "the grouping option is on" true "$(get remote /System/Configuration | jq -r .EnableGroupingMoviesIntoCollections)"

box=$(post remote "/Collections?name=JM%20Saga&ids=$a,$b" | jq -r '.Id // ""')
check "the collection is created" 200 "$(status)"
check "the collection has an id" true "$([ -n "$box" ] && echo true || echo false)"
sleep 3
check "the collection holds both films" 2 "$(get remote "/Items?ParentId=$box" | jq '.Items | length')"

# The sync query of JellyfinRemoteClient.ItemsPath, as the dedicated user, with and without the parameter.
rjm_id=$(get rjm /Users/Me | jq -r .Id)
film_lib=$(get remote /Library/VirtualFolders | jq -r '.[] | select(.Name == "Film") | .ItemId')
sync_query="userId=$rjm_id&recursive=true&includeItemTypes=Movie&sortBy=DateCreated,SortName&sortOrder=Ascending"
sync_query="$sync_query&excludeLocationTypes=Virtual&enableUserData=false"
sync_query="$sync_query&fields=ProviderIds,MediaSources,DateCreated,Etag,Overview,Genres,People,Studios,Tags,Taglines,OriginalTitle,Chapters"
sync_query="$sync_query&startIndex=0&limit=200&parentId=$film_lib"
# summary <answer>: total, the BoxSet rows and whether each film of the collection is in the page.
summary() { jq -c --arg a "$a" --arg b "$b" '{total: .TotalRecordCount, boxsets: [.Items[] | select(.Type == "BoxSet") | .Name],
  filmA: ([.Items[].Id] | index($a) != null), filmB: ([.Items[].Id] | index($b) != null)}'; }

echo "--- the stock answer to the sync query"
without=$(get rjm "/Items?$sync_query" | summary)
with=$(get rjm "/Items?$sync_query&collapseBoxSetItems=false" | summary)
echo "without collapseBoxSetItems: $without"
echo "with collapseBoxSetItems=false: $with"
check "without the parameter the remote hides the films behind the collection" '["JM Saga"] false false' \
  "$(echo "$without" | jq -r '"\(.boxsets | tojson) \(.filmA) \(.filmB)"')"
check "with the parameter the remote answers both films and no collection" '[] true true' \
  "$(echo "$with" | jq -r '"\(.boxsets | tojson) \(.filmA) \(.filmB)"')"
remote_films=$(echo "$with" | jq -r .total)

echo "--- the consumer syncs"
start local jm-col-local-media:/media
library local Film movies /federata/Film
bash "$WT/tests/Integration/scripts/install-plugin.sh" jm-col-local
sleep 3
wait_ready local
login local
settings local '.FederatedFolderPath = "/federata"' >/dev/null
id=$(add_remote local remote http://host.docker.internal:18481 jellymesh)
import_all local "$id"
sync_now local
scan local

for title in "$FILM_A" "$FILM_B"; do
  check "$title has a stub" true "$([ -n "$(strm_file local "$title")" ] && echo true || echo false)"
  check "$title is in the consumer library" true "$([ -n "$(stub_item local "$title")" ] && echo true || echo false)"
done
stubs=$(docker exec jm-col-local sh -c 'find /federata/Film -name "*.strm" | wc -l' | tr -d ' ')
echo "stubs: $stubs, films the remote answers uncollapsed: $remote_films"
check "the consumer has a stub for every film of the remote" true "$([ "$stubs" -ge "$remote_films" ] && echo true || echo false)"
check "no stub stands for the collection" 0 "$(docker exec jm-col-local sh -c 'find /federata -name "*.strm" | grep -c "JM Saga" || true')"

echo "=== failed: $FAILED"
exit $FAILED
