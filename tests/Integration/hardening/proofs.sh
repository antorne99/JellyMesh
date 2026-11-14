#!/usr/bin/env bash
# Whether the limits of the hardening leave real playback alone: the proofs of tests/Integration/provider, unchanged, on
# private servers. PREFIX and PORT_A move their names and ports, because they would collide with anything else using
# them, and a private stock server stands in for the shared stack. Then what those proofs do not cover about a
# peer. Takes about 15 minutes. Containers and volumes are removed at the end.
# usage: proofs.sh   (MEDIA points at tests/Integration/media of the main checkout, scratch goes in WORK;
#   PREFIX, PORT_A and STOCK_PORT move it)
set -uo pipefail
S=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
WT=$(cd "$S/../../.." && pwd)
# The media is not in the repository, so a worktree finds it in the main checkout.
if [ -z "${MEDIA:-}" ]; then
  MEDIA=$WT/tests/Integration/media
  [ -d "$MEDIA" ] || MEDIA=$(cd "$WT/../../.." && pwd)/tests/Integration/media
fi
export MEDIA
export WORK=${WORK:-${TMPDIR:-/tmp}/jm-hard-proofs}
FAILED=0

rm -rf "$WORK"
mkdir -p "$WORK"
# The stock remote is the private one of the proofs (STOCK=own), which draws its Server Id again until it sorts after a.
export PREFIX=${PREFIX:-jm-hard-p} PORT_A=${PORT_A:-18450} STOCK=own STOCK_PORT=${STOCK_PORT:-18457}
PROOFS=$WT/tests/Integration/provider

cleanup() { bash "$PROOFS/down.sh" >/dev/null 2>&1; }
trap cleanup EXIT

cd "$PROOFS" || exit 1
for script in up.sh federate.sh boundaries.sh feed.sh limits.sh network.sh; do
  echo "=== $script"
  bash "./$script" >"$WORK/out-$script" 2>&1
  code=$?
  grep -E '^FAIL' "$WORK/out-$script"
  echo "$script: exit $code, $(grep -c '^PASS' "$WORK/out-$script") passed, $(grep -c '^FAIL' "$WORK/out-$script") failed"
  [ "$code" = 0 ] || FAILED=1
done
echo "=== leaks.sh"
bash ./leaks.sh "$WORK"/out-*.sh >"$WORK/out-leaks" 2>&1 || FAILED=1
grep -E '^FAIL' "$WORK/out-leaks"
echo "leaks.sh: $(grep -c '^PASS' "$WORK/out-leaks") passed, $(grep -c '^FAIL' "$WORK/out-leaks") failed"

bash ./peer-extra.sh || FAILED=1
exit "$FAILED"
