#!/usr/bin/env bash
# Integration tests 2 and 15 of the per-server spec: the stubs of a title on two servers and its local copy are linked as
# versions in process, again after a split or a merge by the admin, with no metadata saver and nothing saved twice.
# Needs up.sh. Run once.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
A=$(cat "$WORK/id-remote")
B=$(cat "$WORK/id-peer")
uid=$(get local /Users/Me | jq -r .Id)
RES="JM Confronto Risoluzione"   # 720p on remote, 1080p on peer
BIT="JM Confronto Bitrate"       # 2.8 on remote, 5.9 on peer
COD="JM Confronto Codec"         # H.264 on remote, AV1 on peer within the bitrate tolerance
LONE="Tears of Steel"            # on remote only

res_a=$(stub_of "$A" "$RES"); res_b=$(stub_of "$B" "$RES")
cod_a=$(stub_of "$A" "$COD"); lone=$(stub_of "$A" "$LONE")
check "the stubs are in the library" 1 "$([ -n "$res_a" ] && [ -n "$res_b" ] && [ -n "$cod_a" ] && [ -n "$lone" ] && echo 1 || echo 0)"

echo "--- before the link: a collection and a playlist on the copies of remote"
post local "/Collections?name=JM%20Raccolta&ids=$(dashed "$res_a")" >/dev/null
post local /Playlists "$(jq -n --arg u "$uid" --arg i "$(dashed "$cod_a")" '{Name: "JM Scaletta", Ids: [$i], UserId: $u, MediaType: "Video"}')" >/dev/null
# Jellyfin adds the items of a new collection after it answers.
for i in $(seq 1 30); do [ -n "$(children_of 'JM Raccolta')" ] && break; sleep 1; done
check "the collection holds the 720p stub" "$res_a" "$(children_of 'JM Raccolta')"
check "the playlist holds the H.264 stub" "$cod_a" "$(children_of 'JM Scaletta')"
nfo_sums >"$WORK/nfo-before"

echo "--- test 2: one group per title, the best copy as primary"
sync_now local
for t in "$RES" "$BIT" "$COD"; do
  a=$(stub_of "$A" "$t"); b=$(stub_of "$B" "$t")
  check "$t: the 1080p or better stub of peer is the primary" "$b" "$(primary_of "$a")"
  check "$t: the primary lists the stub of remote" "$a" "$(alternates_of "$b")"
done
check "a title on one server stays alone" "" "$(primary_of "$lone")$(alternates_of "$lone")"
e5a=$(vid "$(server_folder "$A")/Serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E05")
e5b=$(vid "$(server_folder "$B")/Serie/JM Serie Divisa (2026)/Season 01/JM Serie Divisa S01E05")
check "episodes on both servers are linked" "$e5b" "$(primary_of "$e5a")"
check "one film row for the title" 1 "$(get local "/Items?userId=$uid&Recursive=true&IncludeItemTypes=Movie&searchTerm=Risoluzione" | jq '[.Items[] | select(.Name == "JM Confronto Risoluzione")] | length')"
check "it offers both versions" 2 "$(get local "/Items/$(dashed "$res_b")?userId=$uid" | jq '.MediaSources | length')"
check "the collection moved to the primary" "$res_b" "$(children_of 'JM Raccolta')"
check "the playlist kept the stub that was added" "$cod_a" "$(children_of 'JM Scaletta')"
check "the playlist entry still answers PlaybackInfo" 200 "$(post local "/Items/$(dashed "$cod_a")/PlaybackInfo?userId=$uid" >/dev/null; status)"
nfo_sums >"$WORK/nfo-after"
check "no .nfo was written or changed" "" "$(diff "$WORK/nfo-before" "$WORK/nfo-after" || true)"

echo "--- test 15: relinking saves nothing that is already linked"
members="$res_a $res_b $(stub_of "$A" "$BIT") $(stub_of "$B" "$BIT") $e5a $e5b"
before=$(saved $members)
sync_now local
check "a second sync saves no member" "$before" "$(saved $members)"
scan local
sync_now local
check "a scan keeps the links" "$res_b" "$(primary_of "$res_a")"
before=$(saved $members)
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
sync_now local
check "after a restart a sync saves no member" "$before" "$(saved $members)"

echo "--- test 15: the registry wins over the admin"
bit_a=$(stub_of "$A" "$BIT"); bit_b=$(stub_of "$B" "$BIT")
req local DELETE "/Videos/$(dashed "$bit_b")/AlternateSources" >/dev/null
check "Split versions answers" 204 "$(status)"
check "the split took the link away" "" "$(primary_of "$bit_a")"
sync_now local
check "the next sync links it again" "$bit_b" "$(primary_of "$bit_a")"
check "with the same primary" "$bit_a" "$(alternates_of "$bit_b")"
post local "/Videos/MergeVersions?ids=$(dashed "$bit_b"),$(dashed "$lone")" >/dev/null
check "MergeVersions answers" 204 "$(status)"
check "the merge linked another film" 1 "$( [ -n "$(primary_of "$lone")$(alternates_of "$lone")" ] && echo 1 || echo 0)"
sync_now local
check "the next sync takes the other film out" "" "$(primary_of "$lone")$(alternates_of "$lone")"
check "and the group is as the registry has it" "$bit_b" "$(primary_of "$bit_a")"
check "the primary lists only the stub of remote" "$bit_a" "$(alternates_of "$bit_b")"

echo "--- D4 and Q43: a played local copy joins as the primary"
SIN="Sintel (2010)"
sin_b=$(stub_of "$B" "Sintel")
docker exec "$PREFIX-local" mkdir -p "/media/film/$SIN"
docker cp "$MEDIA/locale/film/$SIN/." "$PREFIX-local:/media/film/$SIN/"
docker exec "$PREFIX-local" sh -c 'chmod -R a+rwX /media/film'
scan local
sin_l=$(vid "/media/film/$SIN/")
check "the local copy is in the library" 1 "$([ -n "$sin_l" ] && echo 1 || echo 0)"
post local "/UserItems/$(dashed "$sin_b")/UserData?userId=$uid" '{"PlaybackPositionTicks": 300000000}' >/dev/null
post local "/UserPlayedItems/$(dashed "$sin_l")?userId=$uid" >/dev/null
check "the stub is not played" false "$(get local "/Users/$uid/Items/$(dashed "$sin_b")" | jq .UserData.Played)"
nfo_sums >"$WORK/nfo-before"
sync_now local
check "the local copy is the primary" "$sin_l" "$(primary_of "$sin_b")"
check "the stub is now played" true "$(get local "/Users/$uid/Items/$(dashed "$sin_b")" | jq .UserData.Played)"
check "its position stayed" 300000000 "$(get local "/Users/$uid/Items/$(dashed "$sin_b")" | jq .UserData.PlaybackPositionTicks)"
nfo_sums >"$WORK/nfo-after"
check "the movie.nfo of the local copy is untouched" "" "$(diff "$WORK/nfo-before" "$WORK/nfo-after" || true)"

exit "$FAILED"
