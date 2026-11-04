#!/usr/bin/env bash
# Decision 17: an upgrade signs stubs that already exist, keeping their items and watch state.
# usage: MEDIA=... upgrade.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

login local
viewer_login
uid=$(user_id local viewer)

list() { get local "/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path&UserId=$uid" | jq -cS '[.Items[] | select((.Path // "") | startswith("/federata")) | {Id, Path, P: .UserData.PlaybackPositionTicks, W: .UserData.Played}] | sort_by(.Id)'; }

tears=$(stub_item local "Tears of Steel")
old_url=$(stub_url local "$tears")
old_strm=$(strm_text local "Tears of Steel")
echo "before: $old_url"
check "the old stub has no signature" "" "$(echo "$old_strm" | grep -o 'sig=' || true)"

# Watch state for the viewer: played on one stub, a position on another.
post local "/UserPlayedItems/$tears?userId=$uid" >/dev/null
sync_stub=$(stub_item local "Titolo Omonimo")
as_viewer POST "/UserItems/$sync_stub/UserData?userId=$uid" '{"PlaybackPositionTicks":200000000}' >/dev/null
list >"$WORK/before.json"
echo "stubs before: $(jq length "$WORK/before.json")"
jq -c '[.[] | select(.W or .P > 0)]' "$WORK/before.json"

NUGET_VOLUME=jellymesh-nuget bash "$WT/tests/Integration/scripts/install-plugin.sh" jm-play-local
sleep 3
wait_ready local
login local

check "the file stays unsigned until the next sync" "" "$(strm_text local "Tears of Steel" | grep -o 'sig=' || true)"
sync_now local

new_strm=$(strm_text local "Tears of Steel")
new_url=$(stub_url local "$tears")
echo "after: $new_url"
check "the file is signed" "1" "$(echo "$new_strm" | grep -c 'sig=')"
check "the item in the database follows the file, without a scan" "$new_strm" "$new_url"
check "the unsigned part is the old URL" "$old_strm" "${new_strm%%\?*}"
check "the .strm holds no remote address" 0 "$(echo "$new_strm" | grep -c 'host.docker.internal\|18431' || true)"


check "every .strm is signed" 0 "$(docker exec jm-play-local sh -c "find /federata/Film -name '*.strm' -exec grep -L 'sig=[0-9a-f]\{64\}$' {} + | wc -l")"

list >"$WORK/after.json"
check "same items, same paths, same watch state" "$(cat "$WORK/before.json")" "$(cat "$WORK/after.json")"

# A later full scan refreshes changed files: nothing may move.
scan local
list >"$WORK/after-scan.json"
check "a full scan changes nothing" "$(cat "$WORK/before.json")" "$(cat "$WORK/after-scan.json")"
check "the item keeps the signed URL after the scan" "$new_strm" "$(stub_url local "$tears")"

echo "key: $(docker exec jm-play-local sh -c 'find /config -name strm-signing.key -exec ls -l {} \;')"
exit $FAILED
