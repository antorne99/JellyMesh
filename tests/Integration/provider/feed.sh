#!/usr/bin/env bash
# The change feed picks up a new item without polling, and a stock remote keeps being polled.
# Runs after federate.sh. usage: MEDIA=... WORK=... feed.sh
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
FAILED=0
TITLE="JM Feed Nuovo"

: >"$WORK/gate-a.log"
: >"$WORK/gate-stock.log"
run_task b "$INCREMENTAL_TASK"
check "nothing changed: the feed is asked" 1 "$(grep -c 'GET /JellyMesh/Provider/Changes' "$WORK/gate-a.log")"
check "nothing changed: no item is read" 0 "$(grep -c 'GET /JellyMesh/Provider/Items' "$WORK/gate-a.log")"

docker exec jm-prov-a sh -c "mkdir -p '/extra/film/$TITLE (2026)' && cp \"\$(find '/media/film/Tears of Steel (2012)' -type f -name '*.m*' | head -1)\" '/extra/film/$TITLE (2026)/$TITLE (2026).mkv'"
scan a

: >"$WORK/gate-a.log"
: >"$WORK/gate-stock.log"
run_task b "$INCREMENTAL_TASK"
scan b

check "the new item reached b" 1 "$(docker exec jm-prov-b sh -c "find /federata -name '$TITLE*.strm' | wc -l" | tr -d ' ')"
check "a is read through the feed" 1 "$(grep -c 'GET /JellyMesh/Provider/Changes' "$WORK/gate-a.log")"
check "a is not polled on the stock catalog" 0 "$(grep -c 'GET /Items ' "$WORK/gate-a.log")"
check "only what changed is read" true "$([ "$(grep -c 'GET /JellyMesh/Provider/Items' "$WORK/gate-a.log")" -le 4 ] && echo true || echo false)"
stock_polls=$(grep -c 'GET /Items ' "$WORK/gate-stock.log")
check "the stock remote is still polled" true "$([ "$stock_polls" -ge 1 ] && echo true || echo false)"

echo "--- a restart of the provider cannot lose a change"
docker restart jm-prov-a >/dev/null
wait_ready a
login a
: >"$WORK/gate-a.log"
run_task b "$INCREMENTAL_TASK"
check "the old cursor is refused and b reads everything" true "$([ "$(grep -c 'GET /JellyMesh/Provider/Items' "$WORK/gate-a.log")" -ge 3 ] && echo true || echo false)"
: >"$WORK/gate-a.log"
run_task b "$INCREMENTAL_TASK"
check "then the feed goes on" 0 "$(grep -c 'GET /JellyMesh/Provider/Items' "$WORK/gate-a.log")"

echo "--- removals stay with the reconciliation"
docker exec jm-prov-a sh -c "rm -rf '/extra/film/$TITLE (2026)'"
scan a
run_task b "$INCREMENTAL_TASK"
check "the feed does not remove it" 1 "$(docker exec jm-prov-b sh -c "find /federata -name '$TITLE*.strm' | wc -l" | tr -d ' ')"
run_task b "$RECONCILE_TASK"
check "the reconciliation does" 0 "$(docker exec jm-prov-b sh -c "find /federata -name '$TITLE*.strm' | wc -l" | tr -d ' ')"

exit "$FAILED"
