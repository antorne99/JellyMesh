#!/usr/bin/env bash
# Decision 17: an upgrade signs stubs that already exist, keeping their items and watch state. Until the migration (F15)
# the stubs of the flat layout stay and work, and the folder of the server is written beside them (Q49).
# usage: MEDIA=... upgrade.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

FLAT=/federata/Film
login local
viewer_login
uid=$(user_id local viewer)

list() { get local "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&UserId=$uid" | jq -cS '[.Items[] | select((.Path // "") | startswith("/federata")) | {Id, Path, P: .UserData.PlaybackPositionTicks, W: .UserData.Played}] | sort_by(.Id)'; }
flat_stubs() { docker exec "$PREFIX-local" sh -c "find $FLAT -name '*.strm' | sort"; }

tears=$(stub_item local "Tears of Steel")
old_url=$(stub_url local "$tears")
old_strm=$(strm_text local "Tears of Steel" "$FLAT")
echo "before: $old_url"
check "the old stub has no signature" "" "$(echo "$old_strm" | grep -o 'sig=' || true)"

# Watch state for the viewer: played on one stub, a position on another.
post local "/UserPlayedItems/$tears?userId=$uid" >/dev/null
sync_stub=$(stub_item local "Titolo Omonimo")
as_viewer POST "/UserItems/$sync_stub/UserData?userId=$uid" '{"PlaybackPositionTicks":200000000}' >/dev/null
list >"$WORK/before.json"
flat_stubs >"$WORK/flat-before.txt"
echo "stubs before: $(jq length "$WORK/before.json")"
jq -c '[.[] | select(.W or .P > 0)]' "$WORK/before.json"

NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" "$PREFIX-local"
sleep 3
wait_ready local
login local

check "the file stays unsigned until the next sync" "" "$(strm_text local "Tears of Steel" "$FLAT" | grep -o 'sig=' || true)"
sync_now local

new_strm=$(strm_text local "Tears of Steel" "$FLAT")
new_url=$(stub_url local "$tears")
echo "after: $new_url"
check "the file is signed" "1" "$(echo "$new_strm" | grep -c 'sig=')"
check "the item in the database follows the file, without a scan" "$new_strm" "$new_url"
check "the unsigned part is the old URL" "$old_strm" "${new_strm%%\?*}"
check "the .strm holds no remote address" 0 "$(echo "$new_strm" | grep -c "host.docker.internal\|$(port_of remote)" || true)"


check "every .strm is signed" 0 "$(docker exec "$PREFIX-local" sh -c "find /federata -name '*.strm' -exec grep -L 'sig=[0-9a-f]\{64\}$' {} + | wc -l")"
check "the flat stubs stay where they were" "$(cat "$WORK/flat-before.txt")" "$(flat_stubs)"

# The sync wrote the folder of the server beside them, with a stub of its own for each film, labelled with its name.
folder=$(server_folder "$(cat "$WORK/id-remote")")
check "the folder of the server has a stub for each flat film" "$(wc -l <"$WORK/flat-before.txt" | tr -d ' ')" \
  "$(docker exec "$PREFIX-local" sh -c "find '$folder/Film' -name '*.strm' | wc -l" | tr -d ' ')"
check "its stub carries the label of the server" "$folder/Film/Tears of Steel (2012)/Tears of Steel (2012) - remote.strm" "$(strm_file local "Tears of Steel")"
check "and is a stub of its own" 1 "$([ "$(strm_text local "Tears of Steel" | sed 's/?.*//')" != "${new_strm%%\?*}" ] && echo 1 || echo 0)"

list >"$WORK/after.json"
check "same items, same paths, same watch state" "$(cat "$WORK/before.json")" "$(cat "$WORK/after.json")"

# A later full scan refreshes changed files: nothing may move.
scan local
list >"$WORK/after-scan.json"
check "a full scan changes nothing" "$(cat "$WORK/before.json")" "$(cat "$WORK/after-scan.json")"
check "the item keeps the signed URL after the scan" "$new_strm" "$(stub_url local "$tears")"

echo "key: $(docker exec "$PREFIX-local" sh -c 'find /config -name strm-signing.key -exec ls -l {} \;')"

# What follows runs on the folder of the server: the admin links it and takes the flat path off the library.
check "the folder of the server joins the library" 204 "$(link_folder local Film "$folder/Film")"
check "the flat path leaves it" 204 "$(unlink_folder local Film "$FLAT")"
scan local
check "the library holds the stub of the server" "$folder/Film/Tears of Steel (2012)/Tears of Steel (2012) - remote.strm" \
  "$(get local "/Items?Ids=$(stub_item local "Tears of Steel")&Fields=Path" | jq -r '.Items[0].Path')"
check "and no flat stub" 0 "$(get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq --arg f "$FLAT/" '[.Items[] | select((.Path // "") | startswith($f))] | length')"
exit $FAILED
