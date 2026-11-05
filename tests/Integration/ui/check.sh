#!/usr/bin/env bash
# All the checks of the configuration page, through every state, or the scenarios named. Exits non-zero on any failure.
# usage: UI_PREFIX=<name> UI_PORT=<port> check.sh [scenario...]
set -uo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

mkdir -p "$WORK/web/shots"
OUT=$WORK/web/check.out
: >"$OUT"

run() { # run <scenario>...: one browser run, whose lines also go to the report
  pw "$UI/pagecheck.py" "$@" 2>&1 | tee -a "$OUT"
  [ "${PIPESTATUS[0]}" = 0 ] || FAILED=1
}

has_key() { docker inspect -f '{{.Config.Env}}' "$(container_of "$1")" | grep -q JELLYMESH_SECRET_KEY; }

# A run stopped halfway leaves a remote down or a key gone.
restore() {
  local s
  for s in fresh first; do
    has_key "$s" || set_key "$s" "$TEST_KEY"
  done
  [ "$(docker inspect -f '{{.State.Running}}' "$(container_of stock)")" = true ] || online stock
}

restore
# A restart forgets the state of the connections, and the checks expect it known.
sync_now fresh
if [ $# -gt 0 ]; then
  run "$@"
else
  run first normal loading errors keyboard themes actions

  set_key first
  run first-nokey
  set_key first "$TEST_KEY"

  set_key fresh
  run nokey
  set_key fresh "$TEST_KEY"

  offline stock
  run offline
  online stock
fi

echo "$(grep -c '^PASS' "$OUT") passed, $(grep -c '^FAIL' "$OUT") failed. Screenshots in $WORK/web/shots"
exit "$FAILED"
