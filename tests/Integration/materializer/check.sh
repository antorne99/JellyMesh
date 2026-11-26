#!/usr/bin/env bash
# Invariants after a scenario: no remote password in the log, no remote address in the plugin data or the folder.
source "$(dirname "${BASH_SOURCE[0]}")"/env.sh
check "log lines with a remote password" 0 "$(applog | grep -c -E 'jellymesh-test' || true)"
echo "NeedsRefresh: $(jm_sql 'select needs_refresh from stub_series order by relative_path;' | paste -sd, -)"
# The hosts and ports of both remotes, dots escaped, as one grep -E pattern.
addr_pat() {
  local u hp pat=
  for u in "$STANDARD_URL" "$JELLYMESH_URL"; do
    hp=${u#*://}; hp=${hp%%/*}
    pat+="${pat:+|}$(printf '%s' "${hp%:*}" | sed 's/\./\\./g')|:${hp##*:}\\b"
  done
  printf '%s' "$pat"
}
pat=$(addr_pat)
# The registry keeps the addresses: every other table is searched.
jm_sql 'select 1;' >/dev/null
echo "plugin data outside the registry with an address: $(sqlite3 "${TMPDIR:-/tmp}/$NAME-jmdb/jellymesh.db" .dump | grep -v '^INSERT INTO servers ' | grep -c -E "$pat" || true)"
check "files in the folder with an address or port" 0 "$(docker exec "$NAME" sh -c 'grep -rlE "$1" /federata --include=*.nfo --include=*.strm | wc -l' sh "$pat" | tr -d ' ')"
exit "${FAILED:-0}"
