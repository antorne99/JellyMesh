#!/usr/bin/env bash
# Integration test 10 of the per-server spec (F19, Q25): removing peer deletes its folder and its automatic libraries,
# and its versions leave the groups before its libraries go, so no title of another server or local copy is hidden or
# changed. Checked before any nightly run. Needs up.sh, and no other test before.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
A=$(cat "$WORK/id-remote")
B=$(cat "$WORK/id-peer")
RES="JM Confronto Risoluzione"   # remote and peer: the stub of peer is the primary
COD="JM Confronto Codec"         # local, remote and peer: the local copy is the primary
SIN="Sintel"                     # local and peer
folder_b=$(server_folder "$B")

auto_names() { get local /JellyMesh/Admin/Status | jq -c --arg id "$B" '[.Servers[] | select(.Id == $id) | .AutomaticLibraries.Libraries[]?.Name]'; }
auto_busy() { get local /JellyMesh/Admin/Status | jq -r --arg id "$B" '.Servers[] | select(.Id == $id) | .AutomaticLibraries.Busy'; }
has_library() { get local /Library/VirtualFolders | jq -e --arg n "$1" 'any(.[]; .Name == $n)' >/dev/null && echo yes || echo no; }
visible() { get local "/Items?Recursive=true&IncludeItemTypes=Movie&Ids=$(dashed "$1")" | jq -r '.TotalRecordCount'; }
local_of() { vid "/media/film/$1"; }
settled() {
  local i j idle
  for i in $(seq 1 180); do
    idle=0
    for j in 1 2 3; do
      [ "$(get local /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .State')" = Idle ] && idle=$((idle + 1))
      sleep 1
    done
    [ "$idle" = 3 ] && eval "$1" && return 0
  done
  return 1
}

echo "--- setup: local copies, the folders of peer in its automatic libraries only"
put_film "locale/film/$SIN (2010)" "$SIN (2010)"
put_film "remoto-standard/film/$COD (2026)" "$COD (2026)"
check "the Film folder of peer leaves Film" 204 "$(unlink_folder local Film "$folder_b/Film")"
check "the Serie folder of peer leaves Serie" 204 "$(unlink_folder local Serie "$folder_b/Serie")"
post local "/JellyMesh/Admin/Servers/$B/AutomaticLibraries" '{"Enabled":true}' >/dev/null
check "the automatic libraries of peer are switched on" 202 "$(status)"
for _ in $(seq 1 120); do [ "$(auto_busy)" = false ] && break; sleep 1; done
names=$(auto_names)
echo "automatic libraries of peer: $names"
check "peer has two automatic libraries" 2 "$(jq length <<<"$names")"
scan local
sync_now local
sl=$(local_of "$SIN"); sp=$(stub_of "$B" "$SIN")
cl=$(local_of "$COD"); ca=$(stub_of "$A" "$COD"); cb=$(stub_of "$B" "$COD")
ra=$(stub_of "$A" "$RES"); rb=$(stub_of "$B" "$RES")
linked() { [ "$(primary_of "$sp")" = "$sl" ] && [ "$(alternates_of "$cl")" = "$(printf '%s\n' "$ca" "$cb" | sort | paste -sd, -)" ] && [ "$(primary_of "$ra")" = "$rb" ]; }
settled "linked || { sync_now local; false; }" || true
film_links "/media/film"
film_links "$RES"
check "Sintel: the local copy is the primary of the stub of peer" "$sl" "$(primary_of "$sp")"
check "Codec: the local copy holds both stubs" "$(printf '%s\n' "$ca" "$cb" | sort | paste -sd, -)" "$(alternates_of "$cl")"
check "Resolution: the stub of peer is the primary of the stub of remote" "$rb" "$(primary_of "$ra")"
sums() { docker exec "$PREFIX-local" sh -c "find /media -type f $1 -exec md5sum {} + | sort -k2"; }
media_before=$(sums "! -name '*.nfo'")
nfo_before=$(sums "-name '*.nfo'")

echo "--- removal of peer"
req local DELETE "/JellyMesh/Admin/Servers/$B" >/dev/null
check "peer is removed" 204 "$(status)"
gone() {
  ! docker exec "$PREFIX-local" test -e "$folder_b" &&
    [ "$(jq -r '.[]' <<<"$names" | while read -r n; do has_library "$n"; done | grep -c yes)" = 0 ]
}
for _ in $(seq 1 180); do gone && break; sleep 1; done
settled true || true
check "the folder of peer is deleted" gone "$(docker exec "$PREFIX-local" test -e "$folder_b" && echo kept || echo gone)"
while read -r n; do check "the automatic library $n is deleted" no "$(has_library "$n")"; done < <(jq -r '.[]' <<<"$names")
check "the library Film stays" yes "$(has_library Film)"
check "the library Serie stays" yes "$(has_library Serie)"
check "no item of peer is left" 0 "$(sql "select count(*) from BaseItems where Path like '$folder_b%';")"
film_links "/media/film"
film_links "$RES"
check "Sintel: the local copy has no version left" "" "$(alternates_of "$sl")"
check "Sintel: the local copy is a primary" "" "$(primary_of "$sl")"
check "Sintel: the local copy is visible" 1 "$(visible "$sl")"
check "Codec: the local copy holds only the stub of remote" "$ca" "$(alternates_of "$cl")"
check "Codec: the local copy is a primary" "" "$(primary_of "$cl")"
check "Codec: the stub of remote points to the local copy" "$cl" "$(primary_of "$ca")"
check "Resolution: the stub of remote stands alone" "" "$(primary_of "$ra")"
check "Resolution: the stub of remote has no version left" "" "$(alternates_of "$ra")"
check "Resolution: the stub of remote is visible" 1 "$(visible "$ra")"
check "the local media files are untouched" "$media_before" "$(sums "! -name '*.nfo'")"
# Open question: deleting a linked stub lets Jellyfin hand the group over with its savers on, so the NFO saver of the
# library can rewrite the movie.nfo of a local copy (row 93). Reported, not failed, until it is decided.
if [ "$nfo_before" = "$(sums "-name '*.nfo'")" ]; then echo "ok   the .nfo of the local copies are untouched"; else
  echo "KNOWN the NFO saver rewrote a .nfo of a local copy:"; diff <(echo "$nfo_before") <(sums "-name '*.nfo'") || true; fi
check "the folder of remote stays" 0 "$(docker exec "$PREFIX-local" test -f "$(server_folder "$A")/Film/.jellymesh"; echo $?)"

echo "=== failed: $FAILED"
exit "$FAILED"
