#!/usr/bin/env bash
# Integration tests 2 and 15 of the per-server spec: the stubs of a title on two servers and its local copy are linked as
# versions in process, again after a split or a merge by the admin, with no metadata saver and nothing saved twice. Then
# what only the nightly run does (Q144, Q146), the links between local films the plugin leaves alone (decision 20, Q148)
# and the NFO saver with SaveImagePathsInNfo on. Needs up.sh. Run once.
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
raccolta=$(post local "/Collections?name=JM%20Raccolta" | jq -r .Id)
post local /Playlists "$(jq -n --arg u "$uid" --arg i "$(dashed "$cod_a")" '{Name: "JM Scaletta", Ids: [$i], UserId: $u, MediaType: "Video"}')" >/dev/null
# 12.4 can lose the items of a new collection to a race on its collection.xml, so they go in afterwards, until they stay.
for i in $(seq 1 30); do
  [ -n "$(children_of 'JM Raccolta')" ] && break
  post local "/Collections/$raccolta/Items?ids=$(dashed "$res_a")" >/dev/null
  sleep 1
done
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
info=$(post local "/Items/$(dashed "$cod_a")/PlaybackInfo?userId=$uid" "$DIRECT")
check "the playlist entry answers PlaybackInfo" 200 "$(status)"
check "with a source and no error" "true null" "$(echo "$info" | jq -r '"\(.MediaSources | length > 0) \(.ErrorCode)"')"
code=$(curl -sS -o /dev/null -w '%{http_code}' -H "Authorization: $AUTH, Token=\"$(token_of local)\"" -H "Range: bytes=0-1023" \
  "http://127.0.0.1:$PORT/Videos/$(dashed "$cod_a")/stream?static=true&mediaSourceId=$(echo "$info" | jq -r '.MediaSources[0].Id')")
check "the playlist entry streams" 206 "$code"
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
check "in every row of its own" 1 "$(sql "select min(Played) from UserData where lower(replace(ItemId, '-', '')) = '$sin_b' and lower(replace(UserId, '-', '')) = '$uid';")"
check "its position stayed" 300000000 "$(get local "/Users/$uid/Items/$(dashed "$sin_b")" | jq .UserData.PlaybackPositionTicks)"
nfo_sums >"$WORK/nfo-after"
check "the movie.nfo of the local copy is untouched" "" "$(diff "$WORK/nfo-before" "$WORK/nfo-after" || true)"

echo "--- Q144 and Q146: what only the nightly run does"
solo=$(stub_of "$B" "JM Solo JellyMesh")
post local "/Videos/MergeVersions?ids=$(dashed "$lone"),$(dashed "$solo")" >/dev/null
check "MergeVersions of two stubs in no group answers" 204 "$(status)"
check "it linked them" 1 "$( [ -n "$(primary_of "$lone")$(primary_of "$solo")" ] && echo 1 || echo 0)"
post local "/Collections/$(dashed "$raccolta")/Items?ids=$(dashed "$bit_a")" >/dev/null
check "an alternate can be added to a collection" 204 "$(status)"
check "the collection holds it" "$res_b,$bit_a" "$(children_of 'JM Raccolta')"
sync_now local
check "a sync leaves the two stubs linked" 1 "$( [ -n "$(primary_of "$lone")$(primary_of "$solo")" ] && echo 1 || echo 0)"
check "a sync leaves the collection entry on the alternate" "$res_b,$bit_a" "$(children_of 'JM Raccolta')"
reconcile
check "the nightly run detached the stub of remote" "" "$(primary_of "$lone")$(alternates_of "$lone")"
check "and the stub of peer" "" "$(primary_of "$solo")$(alternates_of "$solo")"
check "the nightly run moved the entry to the primary" "$res_b,$bit_b" "$(children_of 'JM Raccolta')"
check "the playlist still holds the alternate" "$cod_a" "$(children_of 'JM Scaletta')"
check "the group is still linked" "$bit_b" "$(primary_of "$bit_a")"
members="$lone $solo $res_b $bit_a $bit_b $cod_a"
before=$(saved $members)
reconcile
check "a second nightly run saves nothing" "$before" "$(saved $members)"

echo "--- Q148, decision 20 and NFO: local copies"
ED="Elephants Dream (2006)"
put_film "locale/film/$ED" "$ED"
put_film "remoto-standard/film/$LONE (2012)" "$LONE (2012)"
put_film "remoto-standard/film/$LONE (2012)" "$LONE Copia (2012)"
put_film "remoto-standard/film/JM Versioni Multiple (2026)" "JM Versioni Multiple (2026)"
cur=$(get local /System/Configuration/xbmcmetadata)
post local /System/Configuration/xbmcmetadata "$(echo "$cur" | jq -c '.SaveImagePathsInNfo = true')" >/dev/null
check "SaveImagePathsInNfo is on" true "$(get local /System/Configuration/xbmcmetadata | jq .SaveImagePathsInNfo)"
scan local
ed_l=$(vid "/media/film/$ED/")
tos_1=$(vid "/media/film/$LONE (2012)/"); tos_2=$(vid "/media/film/$LONE Copia (2012)/")
check "the new local films are in the library" 1 "$([ -n "$ed_l" ] && [ -n "$tos_1" ] && [ -n "$tos_2" ] && echo 1 || echo 0)"
echo "file versions of a local film, as 12.4 keeps them after a scan:"
film_links "/media/film/JM Versioni Multiple" | tee "$WORK/multi-before" | sed 's/^/    /'
post local "/Videos/MergeVersions?ids=$(dashed "$sin_l"),$(dashed "$ed_l")" >/dev/null
check "MergeVersions of the local copy with another local film answers" 204 "$(status)"
root=$sin_l; other=$ed_l
[ -n "$(primary_of "$sin_l")" ] && { root=$ed_l; other=$sin_l; }
check "the two films are linked" "$root" "$(primary_of "$other")"
sleep 5
nfo_sums >"$WORK/nfo-before"
sync_now local
for run in sync nightly; do
  [ "$run" = nightly ] && reconcile
  check "$run: the admin merge of two local films stays" "$root" "$(primary_of "$other")"
  check "$run: the stubs of Sintel follow the film it was merged into" "$root" "$(primary_of "$sin_b")"
  check "$run: that film lists the stub and the other local film" "$(printf '%s\n' "$sin_b" "$other" | sort | paste -sd, -)" "$(alternates_of "$root")"
  linked=0; [ "$(primary_of "$lone")" = "$tos_1" ] && linked=1; [ "$(primary_of "$lone")" = "$tos_2" ] && linked=2
  check "$run: one local copy of $LONE is the primary of its stub" 1 "$([ "$linked" != 0 ] && echo 1 || echo 0)"
  spare=$([ "$linked" = 1 ] && echo "$tos_2" || echo "$tos_1")
  check "$run: the other local copy is linked to nothing" "" "$(primary_of "$spare")$(alternates_of "$spare")"
done
film_links "/media/film/JM Versioni Multiple" >"$WORK/multi-after"
echo "after the link:"
sed 's/^/    /' "$WORK/multi-after"
multi_a=$(stub_of "$A" "JM Versioni Multiple")
check "the local copy of JM Versioni Multiple is linked to its stub" 1 "$(grep -c "$(sql "select Path from BaseItems where lower(replace(Id, '-', '')) = '$multi_a';")" "$WORK/multi-after" | awk '{print ($1 > 0)}')"
check "the links among its own files did not change" "$(grep -v /federata "$WORK/multi-before")" "$(grep -v /federata "$WORK/multi-after")"
nfo_sums >"$WORK/nfo-after"
check "with SaveImagePathsInNfo on, no .nfo was written or changed" "" "$(diff "$WORK/nfo-before" "$WORK/nfo-after" || true)"
multi=$(vid "/media/film/JM Versioni Multiple (2026)/JM Versioni Multiple (2026) - 1080p")
dto=$(get local "/Items/$(dashed "$multi")?userId=$uid")
post local "/Items/$(dashed "$multi")" "$(echo "$dto" | jq -c '.Overview = "Edited by hand."')" >/dev/null
sleep 5
nfo_sums >"$WORK/nfo-edit"
check "control: an edit of the same film does rewrite its movie.nfo" 1 \
  "$(diff "$WORK/nfo-after" "$WORK/nfo-edit" | grep -c "JM Versioni Multiple (2026)/movie.nfo" | awk '{print ($1 > 0)}')"

exit "$FAILED"
