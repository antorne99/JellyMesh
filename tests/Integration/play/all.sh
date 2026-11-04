#!/usr/bin/env bash
# The whole proof of tasks 504, 505 and 602, from fresh containers. Leaves them up: lib.sh has down.
# usage: MEDIA=<main checkout>/tests/Integration/media OLD=<checkout of a commit before signing> all.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

failed=0
for step in up upgrade endpoint fallback settings localcopy cycle leaks; do
  echo "=== $step"
  bash "$S/$step.sh" || { failed=1; echo "=== $step FAILED"; }
done
echo "=== finished, failed: $failed"
exit $failed
