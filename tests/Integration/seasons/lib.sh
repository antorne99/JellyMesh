# Helpers for the proofs of seasons and title art. Source it. Builds on play/lib.sh.
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"

# edit <server> <item> <jq filter>: changes an item as the metadata editor does
edit() {
  local dto
  dto=$(get "$1" "/Items/$2" | jq -c "$3")
  post "$1" "/Items/$2" "$dto" >/dev/null
  status
}
# full_scan <server>: scans once no scan runs, until one completes, since a new scan cancels the one running
full_scan() {
  local i
  for i in 1 2 3 4 5; do
    scan_idle "$1"
    scan "$1"
    scan_idle "$1"
    [ "$(scan_task "$1" | jq -r .LastExecutionResult.Status)" = Completed ] && return 0
  done
  echo "no scan of $1 completed" >&2
  return 1
}
scan_task() { get "$1" /ScheduledTasks | jq -c '.[] | select(.Key == "RefreshLibrary")'; }
scan_idle() { until [ "$(scan_task "$1" | jq -r .State)" = Idle ]; do sleep 1; done; }
