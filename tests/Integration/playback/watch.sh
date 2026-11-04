#!/usr/bin/env bash
# Samples ffmpeg on the remotes every 2 s for N seconds, into a file. Usage: watch.sh <seconds> <out>
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
end=$(( $(date +%s) + $1 ))
while [ "$(date +%s)" -lt "$end" ]; do
  echo "--- $(date +%H:%M:%S)"; bash "$S/ffm.sh"; sleep 2
done > "$2" 2>&1
