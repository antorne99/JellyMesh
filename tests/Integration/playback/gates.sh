#!/usr/bin/env bash
# Starts the HTTP gates, kept alive by nohup until killed. GATE_STANDARD and GATE_JELLYMESH (default 18321 and 18322) are
# their ports, STANDARD_PORT and JELLYMESH_PORT (default 8097 and 8098) the remotes behind them.
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
GATE_STANDARD=${GATE_STANDARD:-18321} GATE_JELLYMESH=${GATE_JELLYMESH:-18322}
STANDARD_PORT=${STANDARD_PORT:-8097} JELLYMESH_PORT=${JELLYMESH_PORT:-8098}
pkill -f "httpgate.py $GATE_STANDARD " 2>/dev/null
pkill -f "httpgate.py $GATE_JELLYMESH " 2>/dev/null
mkdir -p "$S/run"; echo up > "$S/run/gate-standard"; echo up > "$S/run/gate-jellymesh"
nohup python3 "$S/httpgate.py" "$GATE_STANDARD" "$STANDARD_PORT" "$S/run/gate-standard" "$S/run/gatelog-standard" >/dev/null 2>&1 &
nohup python3 "$S/httpgate.py" "$GATE_JELLYMESH" "$JELLYMESH_PORT" "$S/run/gate-jellymesh" "$S/run/gatelog-jellymesh" >/dev/null 2>&1 &
sleep 1; curl -s -o /dev/null -w '%{http_code}\n' "http://127.0.0.1:$GATE_STANDARD/System/Info/Public"; curl -s -o /dev/null -w '%{http_code}\n' "http://127.0.0.1:$GATE_JELLYMESH/System/Info/Public"
