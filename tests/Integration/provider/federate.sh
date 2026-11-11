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

# Before the sharing: the dedicated user of a JellyMesh server that has not connected it is an ordinary user.
early=$(add_remote b before-peer "http://$HOST_GATE:$GATE_A" peer)
check "a server not yet connected looks standard" Standard "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$early" '.Servers[] | select(.Id == $i) | .Mode')"
req b DELETE "/JellyMesh/Admin/Servers/$early" >/dev/null

share a 1 Film Serie
share b 2 Film Serie

# The stock remote must sort after a for the limit test: ties between equal copies fall to the server ID.
stock=
for attempt in 1 2 3 4 5 6 7 8; do
  from_a=$(add_remote b a "http://$HOST_GATE:$GATE_A" peer)
  from_stock=$(add_remote b stock "http://$HOST_GATE:$GATE_STOCK" jellymesh)
  [[ "${from_a//-/}" < "${from_stock//-/}" ]] && { stock=$from_stock; break; }
  req b DELETE "/JellyMesh/Admin/Servers/$from_a" >/dev/null
  req b DELETE "/JellyMesh/Admin/Servers/$from_stock" >/dev/null
done
[ -n "$stock" ] || { echo "no server order found" >&2; exit 1; }
from_b=$(add_remote a b "http://$HOST_GATE:$GATE_B" peer)
echo "$from_a" >"$WORK/id-b-from-a"
echo "$from_stock" >"$WORK/id-b-from-stock"
echo "$from_b" >"$WORK/id-a-from-b"

check "b sees a in JellyMesh mode" JellyMesh "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_a" '.Servers[] | select(.Id == $i) | .Mode')"
check "protocol version" 1 "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_a" '.Servers[] | select(.Id == $i) | .ProtocolVersion')"
check "b sees the stock remote in standard mode" Standard "$(get b /JellyMesh/Admin/Status | jq -r --arg i "$from_stock" '.Servers[] | select(.Id == $i) | .Mode')"
check "a sees b in JellyMesh mode" JellyMesh "$(get a /JellyMesh/Admin/Status | jq -r --arg i "$from_b" '.Servers[] | select(.Id == $i) | .Mode')"

import_all b "$from_a"
import_all b "$from_stock"
import_all a "$from_b"
sync_now b
sync_now a
scan b
scan a
exit ${FAILED:-0}
