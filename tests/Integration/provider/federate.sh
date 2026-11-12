#!/usr/bin/env bash
# Federates the two servers with each other, and b with the stock remote too, through the gates.
# usage: MEDIA=... WORK=... federate.sh
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

gates_down
for pair in "a $GATE_A $PORT_A" "b $GATE_B $PORT_B" "stock $GATE_STOCK $STOCK_PORT"; do
  read -r name listen target <<<"$pair"
  gate_mode "$name" up
  : >"$WORK/gate-$name.log"
  nohup python3 "$S/gate.py" "$listen" "$target" "$WORK/gate-$name" "$WORK/gate-$name.log" >/dev/null 2>&1 &
done
sleep 1

share a 1 Film Serie
share b 2 Film Serie

# The stock remote must sort after a for the limit test: ties between equal copies fall to the server ID. A remote
# removed and added again takes back its ID (decision 116), so only a stock remote with a new Server Id draws again.
from_a=$(add_remote b a "http://$HOST_GATE:$GATE_A" peer)
from_stock=$(add_remote b stock "http://$HOST_GATE:$GATE_STOCK" jellymesh)
for attempt in 1 2 3 4 5 6 7; do
  [[ "${from_a//-/}" < "${from_stock//-/}" ]] && break
  [ "$STOCK" = own ] || { echo "the stock remote sorts before a; run with STOCK=own to draw again" >&2; exit 1; }
  req b DELETE "/JellyMesh/Admin/Servers/$from_stock" >/dev/null
  stock_up
  from_stock=$(add_remote b stock "http://$HOST_GATE:$GATE_STOCK" jellymesh)
done
check "a sorts before the stock remote" 1 "$([[ "${from_a//-/}" < "${from_stock//-/}" ]] && echo 1 || echo 0)"
[ "${FAILED:-0}" = 0 ] || exit 1
from_b=$(add_remote a b "http://$HOST_GATE:$GATE_B" peer)
echo "$from_a" >"$WORK/id-b-from-a"
echo "$from_stock" >"$WORK/id-b-from-stock"
echo "$from_b" >"$WORK/id-a-from-b"

check "b sees a in JellyMesh mode" JellyMesh "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_a" '.Servers[] | select(.Id == $i) | .Mode')"
check "protocol version" 2 "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_a" '.Servers[] | select(.Id == $i) | .ProtocolVersion')"
check "b sees the stock remote in standard mode" Standard "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_stock" '.Servers[] | select(.Id == $i) | .Mode')"
check "a sees b in JellyMesh mode" JellyMesh "$(get a /JellyMesh/Admin/Status | jq -r --arg i "$from_b" '.Servers[] | select(.Id == $i) | .Mode')"

# The folders of a server exist once it is added: the libraries take them, as an admin would.
link_server b "$from_a"
link_server b "$from_stock"
link_server a "$from_b"
import_all b "$from_a"
import_all b "$from_stock"
import_all a "$from_b"
sync_now b
sync_now a
scan b
scan a
exit ${FAILED:-0}
