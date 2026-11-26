#!/usr/bin/env bash
# A pure rename of a server over a title merged by hand (§10 rows 572, 575): the stub that joined the group keeps the
# record of the merge, yet the rename copies its whole row, not watched included, and a restart and a sync keep it.
# Needs up.sh. Run once.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
login local
uid=$(get local /Users/Me | jq -r .Id)
remote=$(cat "$WORK/id-remote")
NEW_NAME="Remoto Rinominato"

# carries <sql>: runs a query on a fresh copy of the plugin database of the local server, with its write-ahead log.
carries() {
  local d f
  d=$(mktemp -d "$WORK/jm-db.XXXXXX")
  for f in "" -wal -shm; do
    docker cp "$PREFIX-local:$DATA/jellymesh.db$f" "$d/jellymesh.db$f" >/dev/null 2>&1 || true
  done
  sqlite3 "$d/jellymesh.db" "$1"
}
carry_count() { carries "select count(*) from watch_state_carries where user_id = '$uid' and item_id = '$1';"; }
carry_operation() { carries "select coalesce(operation, 'none') from watch_state_carries where user_id = '$uid' and item_id = '$1';"; }
rename_busy() { get local /JellyMesh/Admin/Status | jq -r --arg id "$remote" '.Servers[] | select(.Id == $id) | .Rename.Busy'; }
wait_rename() {
  local i
  for i in $(seq 1 600); do
    [ "$(rename_busy)" = false ] && return 0
    sleep 1
  done
  echo "the rename did not end" >&2
  return 1
}

echo "--- the merge: Tre goes into Uno, Uno joins the group of Due and leaves the record of the merge"
uno=$(film_of remote "JM Catena Uno"); tre=$(film_of remote "JM Catena Tre"); due=$(film_of peer "JM Catena Due")
found "the stubs of Uno, Tre and Due" "$uno" "$tre" "$due"
post local "/UserPlayedItems/$(dashed "$uno")?userId=$uid" >/dev/null
post local "/UserPlayedItems/$(dashed "$tre")?userId=$uid" >/dev/null
check "the admin has watched Uno and Tre" "true true" "$(state "$uno" | jq .Played) $(state "$tre" | jq .Played)"
check "the merge is accepted" 204 "$(merge local '^JM Catena')"
applied local
check "Uno is a version of Due" "$due:$uno" "$(group "$uno")"
check "Due took the state of Uno (decision 172)" true "$(state "$due" | jq .Played)"
check "the carry from Uno is recorded as a merge" "1 Merge" "$(carry_count "$uno") $(carry_operation "$uno")"
# Jellyfin keeps the rows of the removed Tre aside and reattaches them by key to a new item with its IDs, as Uno has now.
check "Jellyfin keeps a watched row of Tre aside" true "$([ "$(sql "select count(*) from UserData where Played = 1 and ItemId = '00000000-0000-0000-0000-000000000001';")" -gt 0 ] && echo true || echo false)"

echo "--- the admin marks Uno not watched, then renames its server"
req local DELETE "/UserPlayedItems/$(dashed "$uno")?userId=$uid" >/dev/null
check "Uno is not watched, nor is the title" '{"Played":false,"PlayCount":0} false' "$(state "$uno" | jq -c '{Played, PlayCount}') $(state "$due" | jq .Played)"
check "the record of the merge is still there" "1 Merge" "$(carry_count "$uno") $(carry_operation "$uno")"
check_server local "$remote" "{\"Name\":\"$NEW_NAME\"}" >/dev/null
check "the check of the rename answers" 200 "$(status)"
save_server local "$remote" "{\"Name\":\"$NEW_NAME\"}" >/dev/null
check "the rename is saved" 200 "$(status)"
wait_rename
renamed=$(film_of remote "JM Catena Uno")
check "Uno has a new item under the new label" true "$([ -n "$renamed" ] && [ "$renamed" != "$uno" ] && echo true || echo false)"
check "the old item is gone" "" "$(sql "select Id from BaseItems where lower(replace(Id, '-', '')) = '$uno';")"
check "the strm of Uno carries the new label" 1 "$(docker exec "$PREFIX-local" sh -c "find /federata -name '*JM Catena Uno*- $NEW_NAME.strm' | wc -l" | tr -d ' ')"

renamed_state() { # renamed_state <when>
  check "$1: the renamed Uno stays not watched (row 575)" '{"Played":false,"PlayCount":0}' "$(state "$renamed" | jq -c '{Played, PlayCount}')"
  check "$1: the record went with the old item (decision 179)" 0 "$(carry_count "$uno")"
  check "$1: nor is the title" false "$(state "$due" | jq .Played)"
}
renamed_state "after the rename"

echo "--- a restart, then a sync and a scan"
docker restart "$PREFIX-local" >/dev/null
sleep 3
wait_ready local
login local
renamed_state "after a restart"
sync_now local; scan local; settle local
renamed_state "after a sync and a scan"
exit "$FAILED"
