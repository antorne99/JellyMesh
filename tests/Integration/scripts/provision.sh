#!/usr/bin/env bash
#
# Provisions the three instances: first-run wizard, admin, libraries, the
# non-admin user JellyMesh logs in with on remotes (plan §3.1), then a scan.
#
# Safe to re-run: finished steps are skipped and a new scan is started.

set -euo pipefail

source "$(dirname "$0")/common.sh"

wait_ready() {
  local i
  # /health answers while Jellyfin still serves its "starting" page, so wait
  # for an endpoint that only the real server provides.
  for i in $(seq 1 120); do
    get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1 && return
    sleep 1
  done
  echo "$BASE did not become ready" >&2
  exit 1
}

run_wizard() {
  local done
  done=$(get /System/Info/Public | jq -r .StartupWizardCompleted)
  [ "$done" = true ] && return
  echo "  first-run wizard"
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}'
  # The wizard creates the first user on this read and renames it on the post.
  get /Startup/User >/dev/null
  post /Startup/User "$(jq -n --arg n "$ADMIN_USER" --arg p "$ADMIN_PASSWORD" '{Name:$n,Password:$p}')"
  post /Startup/Complete
}

# add_library <name> <collection type> <path>...
add_library() {
  local name=$1 type=$2 query path
  shift 2
  if get /Library/VirtualFolders | jq -e --arg n "$name" 'any(.[]; .Name == $n)' >/dev/null; then
    return
  fi
  echo "  library $name: $*"
  query="name=$name&collectionType=$type&refreshLibrary=false"
  for path in "$@"; do query+="&paths=$path"; done
  post "/Library/VirtualFolders?$query" '{"LibraryOptions":{"EnableInternetProviders":true,"PreferredMetadataLanguage":"it","MetadataCountryCode":"IT"}}'
}

# link_server_folders: adds the Film and Serie folders of each remote server already added to the libraries.
link_server_folders() {
  local dir kind library path
  for dir in "$(dirname "$0")"/../federata/*-*-*-*-*/; do
    [ -d "$dir" ] || continue
    for kind in Film Serie; do
      path=/federata/$(basename "$dir")/$kind
      library=$(get /Library/VirtualFolders | jq -c --arg n "$kind" '.[] | select(.Name == $n)')
      echo "$library" | jq -e --arg p "$path" '.Locations | index($p)' >/dev/null && continue
      echo "  library $kind: $path"
      post "/Library/VirtualFolders/Paths?refreshLibrary=false" "$(jq -n --arg n "$kind" --arg p "$path" '{Name: $n, PathInfo: {Path: $p}}')"
    done
  done
}

add_remote_user() {
  if get /Users | jq -e --arg n "$REMOTE_USER" 'any(.[]; .Name == $n)' >/dev/null; then
    return
  fi
  echo "  user $REMOTE_USER (non-admin)"
  post /Users/New "$(jq -n --arg n "$REMOTE_USER" --arg p "$REMOTE_PASSWORD" '{Name:$n,Password:$p}')" >/dev/null
}

scan_and_wait() {
  local last new i
  last=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  echo "  scanning"
  post /Library/Refresh
  for i in $(seq 1 600); do
    new=$(scan_task | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && return
    sleep 1
  done
  echo "scan did not finish on $BASE" >&2
  exit 1
}

scan_task() {
  get /ScheduledTasks | jq '.[] | select(.Key == "RefreshLibrary")'
}

for instance in locale remoto-standard remoto-jellymesh; do
  echo "$instance"
  use_instance "$instance"
  TOKEN=
  wait_ready
  run_wizard
  login
  if [ "$instance" = locale ]; then
    add_library Film movies /media/film /federata/Film
    add_library Serie tvshows /media/serie /federata/Serie
    link_server_folders
  else
    add_library Film movies /media/film
    add_library Serie tvshows /media/serie
    add_remote_user
  fi
  scan_and_wait
done

echo "provisioned; run scripts/report.sh to see what each server indexed"
