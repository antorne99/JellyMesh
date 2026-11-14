#!/usr/bin/env bash
# Manual merges of series (decision 51): two series of one title with different IDs, one on each remote, are a case; the
# merge joins them per episode, links only the episodes of one interval (D3) and carries their state to the primary
# (decision 172); the merge holds after a restart, and an undo dissolves the episode group at once (Q171).
# Needs up.sh. Run once, after run.sh or alone.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
uid=$(get local /Users/Me | jq -r .Id)
A=$(cat "$WORK/id-remote")
B=$(cat "$WORK/id-peer")

SD="$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
HD="$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"

# add_show <server> <clip> <tmdb> <episode>...: JM Spola in /extra/serie, with its tvshow.nfo.
add_show() {
  local name s=$1 clip=$2 tmdb=$3 folder="/extra/serie/JM Spola (2026)" e
  shift 3
  name=$(name_of "$s")
  docker exec "$name" mkdir -p "$folder/Season 01"
  printf '%s' "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><tvshow><title>JM Spola</title><year>2026</year><uniqueid type=\"tmdb\" default=\"true\">$tmdb</uniqueid></tvshow>" |
    docker exec -i "$name" sh -c "cat >'$folder/tvshow.nfo'"
  for e in "$@"; do
    docker cp "$clip" "$name:$folder/Season 01/JM Spola S01E0$e.mkv" >/dev/null
  done
  docker exec "$name" chmod -R a+rwX /extra/serie
}

# episode_of <server id> <episode>: the id of an episode stub in the folder of a server, hidden alternates included.
episode_of() { vid "$(server_folder "$1")/Serie/JM Spola%S01E0$2"; }
series_cases() { list local | jq -c '[.SeriesCases[] | select(any(.Titles[].Copies[]; .Copy.Title | test("JM Spola")))]'; }
series_keys() { series_cases | jq -c '{WorkKeys: [.[].Titles[].WorkKey]}'; }
series_merge_id() { list local | jq -r '[.Merges[] | select(any(.Copies[]; .Copy.Title | test("JM Spola"))) | .Id] | first'; }

echo "--- two series of one title with different IDs: S01E01 and S01E02 on remote, S01E01 and S01E03 on peer"
for s in remote peer; do
  docker exec "$PREFIX-$s" sh -c 'mkdir -p /extra/serie && chmod a+rwx /extra/serie'
done
add_show remote "$SD" 999100041 1 2
add_show peer "$HD" 999100042 1 3
for s in remote peer; do
  library "$s" Serie tvshows /extra/serie
  scan "$s"
  import_all local "$(cat "$WORK/id-$s")"
done
sync_now local
library local Serie tvshows "$(server_folder "$A")/Serie" "$(server_folder "$B")/Serie"
scan local
sync_now local
settle local
a1=$(episode_of "$A" 1); a2=$(episode_of "$A" 2); b1=$(episode_of "$B" 1); b3=$(episode_of "$B" 3)
check "four episode stubs" 1 "$([ -n "$a1" ] && [ -n "$a2" ] && [ -n "$b1" ] && [ -n "$b3" ] && echo 1 || echo 0)"
check "none linked" ":" "$(primary_of "$a1")$(primary_of "$b1"):$(alternates_of "$a1")$(alternates_of "$b1")"

echo "--- the list"
series_cases | jq -c '.[] | {ContentKind, Titles: [.Titles[] | [.Copies[] | "\(.Copy.Title) \(.ServerName // "local")"]]}'
check "the two series are one case of two titles" 2 "$(series_cases | jq '[.[].Titles[]] | length')"
check "the case says it is of series" '["Episode"]' "$(series_cases | jq -c '[.[].ContentKind]')"
check "one copy per series, each on its server" '["peer","remote"]' "$(series_cases | jq -c '[.[].Titles[].Copies[].ServerName] | sort')"
check "and none among the film cases" 0 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title | test("JM Spola"))] | length')"
check "the list holds no address" 0 "$(list local | grep -c "host.docker.internal\|:$((PORT + 1))\|:$((PORT + 2))" || true)"

echo "--- merging: S01E01 becomes one episode in two versions, S01E02 and S01E03 stay apart"
post local "/UserPlayedItems/$(dashed "$a1")?userId=$uid" >/dev/null
post local "/UserFavoriteItems/$(dashed "$a1")?userId=$uid" >/dev/null
check "the merge is accepted" 204 "$(post local /JellyMesh/Admin/Merges "$(series_keys)" >/dev/null; status)"
check "the episodes of one interval are linked at once, the best as primary" "$b1:$a1" "$(group "$a1")"
applied local
check "every stub stays, as the same item" "$a1 $a2 $b1 $b3" "$(episode_of "$A" 1) $(episode_of "$A" 2) $(episode_of "$B" 1) $(episode_of "$B" 3)"
check "the group holds after the scan" "$b1:$a1" "$(group "$a1")"
check "the episodes of other intervals stay alone (D3)" "::" "$(primary_of "$a2"):$(primary_of "$b3"):$(alternates_of "$a2")$(alternates_of "$b3")"
check "the primary takes favorite and play count of the episode that joined" '{"Played":true,"PlayCount":1,"Favorite":true}' "$(state "$b1")"
check "the decisions are manual" '["Manual"]' "$(get local /JellyMesh/Admin/Index/Decisions | jq -c '[.Contents[] | select(.Identity.Key | contains("99910004")) | .Identity.Match] | unique')"
check "three episodes for the title" 3 "$(get local /JellyMesh/Admin/Index/Decisions | jq '[.Contents[] | select(.Identity.Key | contains("99910004"))] | length')"
check "the case is gone" "[]" "$(series_cases)"
echo "evidence: series rows a client sees for the title: $(get local "/Items?userId=$uid&Recursive=true&IncludeItemTypes=Series&searchTerm=Spola" | jq '[.Items[] | select(.Name == "JM Spola")] | length')"
check "the primary offers both versions" 2 "$(get local "/Items/$(dashed "$b1")?userId=$uid" | jq '.MediaSources | length')"
info=$(post local "/Items/$(dashed "$b1")/PlaybackInfo?userId=$uid" "$DIRECT")
check "no remote address in PlaybackInfo" 0 "$(echo "$info" | grep -c "host.docker.internal\|:$((PORT + 1))\|:$((PORT + 2))" || true)"

echo "--- a restart and a sync"
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
check "the merge is back and in effect" true "$(list local | jq --arg i "$(series_merge_id)" '.Merges[] | select(.Id == $i) | .IsInEffect')"
sync_now local; scan local; sync_now local
check "the group holds" "$b1:$a1" "$(group "$a1")"
check "the episodes of other intervals stay alone" "::" "$(primary_of "$a2"):$(primary_of "$b3"):$(alternates_of "$a2")$(alternates_of "$b3")"

echo "--- undoing"
check "the undo is accepted" 204 "$(req local DELETE "/JellyMesh/Admin/Merges/$(series_merge_id)" >/dev/null; status)"
check "the episode group is dissolved at once, by detaching its stubs (Q171)" ":" "$(primary_of "$a1")$(primary_of "$b1"):$(alternates_of "$a1")$(alternates_of "$b1")"
applied local
check "every stub stays, as the same item" "$a1 $a2 $b1 $b3" "$(episode_of "$A" 1) $(episode_of "$A" 2) $(episode_of "$B" 1) $(episode_of "$B" 3)"
check "the series are listed again" 2 "$(series_cases | jq '[.[].Titles[]] | length')"
sync_now local
check "and the group stays dissolved after a sync" ":" "$(primary_of "$a1")$(primary_of "$b1"):$(alternates_of "$a1")$(alternates_of "$b1")"

exit "$FAILED"
