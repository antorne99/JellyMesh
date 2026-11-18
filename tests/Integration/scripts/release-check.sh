#!/usr/bin/env bash
#
# Checks a release zip on the oldest supported Jellyfin (12.1) before it ships (spec 0.6, §3.19, D18): the DLL asks for
# the Microsoft.Data.Sqlite that 12.1 carries, the server starts with the plugin, the plugin is Active, and its store
# opens with no SQLite load error. Without a zip it builds one from this tree as the release workflow does.
#
# usage: release-check.sh [path/to/jellymesh_<version>.zip]
#
# The release-12-1 service of docker-compose.yml is created fresh, with fresh volumes, and removed at the end; KEEP=1
# leaves it running. The other services of the stack are never touched. Needs Docker, curl, jq, unzip, and zip, md5sum
# and sha256sum to build the zip. The build and the DLL check run in the jellymesh-sdk container (see CLAUDE.md).
#
# STORE_FILE and SEED_STORE name the plugin's store and say whether to seed it. 0.5 opens watch-state.db only when the
# file exists, so an empty one is seeded; a release that makes its own database at start sets SEED_STORE=0.

set -uo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
repo=$(cd "$here/../.." && pwd)
# shellcheck source=common.sh
source "$here/scripts/common.sh"

service=release-12-1
project=jellymesh-test
container=$project-$service-1
volumes=("${project}_$service-config" "${project}_$service-cache")
port=${RELEASE_PORT:-8099}
sdk=${SDK_CONTAINER:-jellymesh-sdk}
expected_sqlite=${EXPECTED_SQLITE:-10.0.11.0}
store_file=${STORE_FILE:-watch-state.db}
seed_store=${SEED_STORE:-1}
startup_timeout=${STARTUP_TIMEOUT:-180}
store_timeout=${STORE_TIMEOUT:-120}
data_folder=/config/plugins/Jellyfin.Plugin.JellyMesh
log=${TMPDIR:-/tmp}/jellymesh-release-check.log
BASE=http://127.0.0.1:$port

work=$(mktemp -d)
failures=0
results=()

compose() {
  docker compose -f "$here/docker-compose.yml" --profile release "$@"
}

# Removes only this service and its volumes; never "down", which would stop the rest of the stack.
remove_instance() {
  compose rm -s -f "$service" >/dev/null 2>&1
  docker volume rm -f "${volumes[@]}" >/dev/null 2>&1
}

finish() {
  [ -n "$(docker ps -aq -f "name=^$container\$")" ] && docker logs "$container" >"$log" 2>&1
  if [ "${KEEP:-0}" = 1 ]; then
    echo "KEEP=1: $container left running on $BASE"
  else
    remove_instance
  fi
  rm -rf "$work"
}
trap finish EXIT

pass() { results+=("PASS  $1"); echo "PASS  $1"; }
fail() { results+=("FAIL  $1: $2"); echo "FAIL  $1: $2" >&2; failures=$((failures + 1)); }

# The path of the repository inside the SDK container, which mounts the main checkout at /src.
sdk_path() {
  local mount
  mount=$(docker inspect "$sdk" --format '{{range .Mounts}}{{if eq .Destination "/src"}}{{.Source}}{{end}}{{end}}') || return 1
  case $repo/ in
    "$mount"/*) echo "/src${repo#"$mount"}" ;;
    *) echo "$repo is not under $mount, the /src of $sdk; start $sdk from the main checkout" >&2; return 1 ;;
  esac
}

# Builds the Release DLL in the SDK container and packs it with the release workflow's package.sh.
build_zip() {
  local path packed
  docker start "$sdk" >/dev/null || return 1
  path=$(sdk_path) || return 1
  rm -rf "$repo/artifacts/build"
  echo "building the release zip in $sdk" >&2
  docker exec -w "$path" "$sdk" dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj \
    -c Release -o artifacts/build --nologo -v quiet >&2 || return 1
  packed=$("$repo/.github/scripts/package.sh") || return 1
  echo "$repo/$(head -1 <<<"$packed")"
}

# Prints the version each assembly reference of the DLL asks for, one "name version" per line.
assembly_references() {
  local copy=/tmp/jellymesh-release-check.dll
  docker start "$sdk" >/dev/null && docker cp "$1" "$sdk:$copy" >/dev/null || return 1
  docker exec -e DLL="$copy" "$sdk" pwsh -NoProfile -NonInteractive -Command '
    $stream = [IO.File]::OpenRead($env:DLL)
    $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader([Reflection.PortableExecutable.PEReader]::new($stream))
    foreach ($handle in $metadata.AssemblyReferences) {
      $reference = $metadata.GetAssemblyReference($handle)
      "{0} {1}" -f $metadata.GetString($reference.Name), $reference.Version
    }'
  docker exec "$sdk" rm -f "$copy"
}

check_sqlite_reference() {
  local name="the DLL asks for Microsoft.Data.Sqlite $expected_sqlite" references found
  if ! references=$(assembly_references "$work/zip/$dll") || [ -z "$references" ]; then
    fail "$name" "the references of $dll could not be read in $sdk"
    return
  fi
  found=$(awk '$1 == "Microsoft.Data.Sqlite" { print $2 }' <<<"$references")
  if [ "$found" = "$expected_sqlite" ]; then
    pass "$name"
  else
    fail "$name" "it asks for ${found:-no Microsoft.Data.Sqlite}"
  fi
}

# Puts the plugin where Jellyfin installs it, and the seeded store in the plugin's data folder, before the first start.
install_plugin() {
  local stage=$work/stage
  mkdir -p "$stage/plugins/JellyMesh_$version" || return 1
  cp "$work/zip/"* "$stage/plugins/JellyMesh_$version/" || return 1
  if [ "$seed_store" = 1 ]; then
    mkdir -p "$stage/plugins/Jellyfin.Plugin.JellyMesh" && : >"$stage/plugins/Jellyfin.Plugin.JellyMesh/$store_file" || return 1
  fi
  compose up --no-start --no-deps "$service" >/dev/null 2>&1 || return 1
  docker cp "$stage/plugins" "$container:/config/" >/dev/null
}

check_startup() {
  local name="Jellyfin 12.1 starts with the plugin" i
  docker start "$container" >/dev/null || { fail "$name" "the container did not start"; return 1; }
  for ((i = 0; i < startup_timeout; i++)); do
    if [ "$(docker inspect -f '{{.State.Running}}' "$container" 2>/dev/null)" != true ]; then
      fail "$name" "the server stopped (exit code $(docker inspect -f '{{.State.ExitCode}}' "$container"))"
      return 1
    fi
    # /health answers on the "starting" page too, so wait for an endpoint only the real server provides.
    if get /System/Info/Public 2>/dev/null | jq -e .Version >/dev/null 2>&1; then
      pass "$name ($(get /System/Info/Public | jq -r .Version))"
      return 0
    fi
    sleep 1
  done
  fail "$name" "no answer on $BASE after ${startup_timeout}s"
  return 1
}

run_wizard() {
  post /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"IT","PreferredMetadataLanguage":"it"}' >/dev/null &&
    get /Startup/User >/dev/null &&
    post /Startup/User "$(jq -n --arg n "$ADMIN_USER" --arg p "$ADMIN_PASSWORD" '{Name:$n,Password:$p}')" >/dev/null &&
    post /Startup/Complete >/dev/null &&
    login
}

# 12.1 lists a plugin it skipped (an unsupported targetAbi) as Active too, so the load line of the log must be there.
check_plugin_active() {
  local name="the plugin $version is loaded and Active" plugin status server_log
  server_log=$(docker logs "$container" 2>&1)
  if ! grep -qF "Loaded plugin: JellyMesh $version" <<<"$server_log"; then
    fail "$name" "the log has no \"Loaded plugin: JellyMesh $version\"$(grep -m1 -F ' of JellyMesh' <<<"$server_log" | sed 's/^/; it says: /')"
    return
  fi
  plugin=$(get /Plugins | jq -c --arg id "${guid//-/}" '.[] | select((.Id | ascii_downcase | gsub("-"; "")) == $id)') || {
    fail "$name" "/Plugins did not answer"
    return
  }
  status=$(jq -r '"\(.Status) \(.Version)"' <<<"$plugin" 2>/dev/null)
  if [ "$status" = "Active $version" ]; then
    pass "$name"
  else
    fail "$name" "Jellyfin reports ${status:-no JellyMesh plugin}"
  fi
}

# The first bytes of the store file, or nothing while it is missing or empty.
store_header() {
  docker exec "$container" sh -c 'head -c 15 "$1" 2>/dev/null' sh "$data_folder/$store_file"
}

# The store may open at start (0.5 does it in the first sync); otherwise a library scan runs the post-scan task, which
# opens it when the federated folder is set.
check_store_opens() {
  local name="the store $store_file opens" i
  if [ "$(store_header)" = "SQLite format 3" ]; then
    pass "$name (at start)"
    return
  fi
  post /Library/Refresh >/dev/null || { fail "$name" "the library scan did not start"; return; }
  for ((i = 0; i < store_timeout; i++)); do
    if [ "$(store_header)" = "SQLite format 3" ]; then
      pass "$name (after a library scan)"
      return
    fi
    sleep 1
  done
  fail "$name" "$data_folder/$store_file has no SQLite schema after ${store_timeout}s"
}

# Waits up to a minute for the scheduled tasks, such as the first sync and a scan, to finish before the log is read.
wait_tasks_idle() {
  local i
  for ((i = 0; i < 60; i++)); do
    [ "$(get /ScheduledTasks 2>/dev/null | jq '[.[] | select(.State != "Idle")] | length')" = 0 ] && return
    sleep 1
  done
  echo "some scheduled tasks were still running after 60s" >&2
}

check_log() {
  local name="no SQLite load error or plugin error in the log" errors
  errors=$(docker logs "$container" 2>&1 |
    grep -E 'FileLoadException|FileNotFoundException|Could not load file or assembly|DllNotFoundException|TypeLoadException|e_sqlite3|SqliteException|\[(ERR|FTL)\].*JellyMesh')
  if [ -z "$errors" ]; then
    pass "$name"
  else
    fail "$name" "$(printf '\n%s' "$errors" | head -20)"
  fi
}

zip=${1:-}
if [ -z "$zip" ]; then
  zip=$(build_zip) || { echo "the release zip could not be built" >&2; exit 2; }
fi
[ -f "$zip" ] || { echo "no zip at $zip" >&2; exit 2; }
mkdir -p "$work/zip" && unzip -q "$zip" -d "$work/zip" || { echo "$zip is not a zip" >&2; exit 2; }
version=$(jq -r .version "$work/zip/meta.json") && guid=$(jq -r .guid "$work/zip/meta.json") &&
  dll=$(jq -r '.assemblies[0]' "$work/zip/meta.json") || { echo "$zip has no readable meta.json" >&2; exit 2; }
[ -f "$work/zip/$dll" ] || { echo "$zip has no $dll" >&2; exit 2; }
echo "checking $zip ($version) on $service"

check_sqlite_reference

remove_instance
if ! install_plugin; then
  fail "the plugin installs into $service" "the container could not be created or filled"
elif check_startup; then
  if run_wizard; then
    check_plugin_active
    check_store_opens
    wait_tasks_idle
  else
    fail "the first-run wizard and the admin login" "they failed, so the plugin and its store were not checked"
  fi
  check_log
else
  fail "the plugin is Active and its store opens" "not checked, the server did not start"
  echo "last lines of the server log:" >&2
  docker logs --tail 40 "$container" 2>&1 | sed 's/^/  /' >&2
fi

echo
echo "release check of $version on Jellyfin 12.1:"
printf '  %s\n' "${results[@]}"
echo "full server log: $log"
if [ "$failures" -gt 0 ]; then
  echo "$failures check(s) failed: do not release this zip" >&2
  exit 1
fi
echo "all checks passed"
