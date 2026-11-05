#!/usr/bin/env bash
# Starts the HTTP gates, kept alive by nohup until killed.
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
pkill -f httpgate.py 2>/dev/null
mkdir -p "$S/run"; echo up > "$S/run/gate-standard"; echo up > "$S/run/gate-jellymesh"
nohup python3 "$S/httpgate.py" 18321 8097 "$S/run/gate-standard" "$S/run/gatelog-standard" >/dev/null 2>&1 &
nohup python3 "$S/httpgate.py" 18322 8098 "$S/run/gate-jellymesh" "$S/run/gatelog-jellymesh" >/dev/null 2>&1 &
sleep 1; curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:18321/System/Info/Public; curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:18322/System/Info/Public
