#!/usr/bin/env bash
# Every scenario on the final code, each on a fresh instance.
D=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
for s in s1 s2 s3 s4 s5; do
  bash "$D/up.sh" >"$D/up-$s.log" 2>&1 || { echo "up failed for $s"; continue; }
  bash "$D/$s.sh" >"$D/final-$s.out" 2>&1
  echo "$s exit $?"
done
