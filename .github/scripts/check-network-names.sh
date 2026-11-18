#!/usr/bin/env bash
#
# Fails when a tracked file names one of the networks in NETWORK_NAMES, separated by commas or new lines, ignoring case.
# It prints only file and line of each match, never the names. With NETWORK_NAMES empty it warns and passes.
#
# usage: NETWORK_NAMES=... check-network-names.sh

set -euo pipefail

cd "$(dirname "$0")/../.."

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

printf '%s\n' "${NETWORK_NAMES:-}" | tr ',\r' '\n\n' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' | { grep -v '^$' || true; } >"$work/names"

if [ ! -s "$work/names" ]; then
  echo "::warning::NETWORK_NAMES is empty, so no network name was looked for."
  exit 0
fi

status=0
git grep -I -i -n -F -f "$work/names" -- . >"$work/hits" || status=$?

case $status in
  0)
    cut -d: -f1,2 "$work/hits" | while IFS=: read -r file line; do
      echo "::error file=$file,line=$line::A network name is on this line."
      echo "$file:$line"
    done
    echo "A network name is in $(cut -d: -f1 "$work/hits" | sort -u | wc -l | tr -d ' ') tracked files."
    exit 1
    ;;
  1)
    echo "No network name found in $(git ls-files | wc -l | tr -d ' ') tracked files."
    ;;
  *)
    echo "git grep failed."
    exit "$status"
    ;;
esac
