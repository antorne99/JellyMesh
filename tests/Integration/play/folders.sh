#!/usr/bin/env bash
# Integration 9 of the 0.6 spec (F44, §10 rows 444-445): the folder migration to the remote library layout. Four
# starting points, each made with the real older builds: a 0.5 that migrated automatically, one that migrated by hand,
# a 0.4 flat layout and a 0.5 automatic migration stopped half-way. The 0.5 choice is kept, the admin's libraries get the
# type folders in place of the old folders, the watch state goes over, and a title in two remote libraries of one local
# library is one stub with its state once (D19). By hand, a library JellyMesh made shows each title twice until the
# carry (D61), then moves to the type folders with no sync after it (F45).
#   local   $PREFIX-local   $PORT    plugin, consumer of Casa and Mare, user alice
#   remote  $PREFIX-remote  $PORT+1  Casa: stock, remoto-standard; "Film 4K" lists the same films as "Film"
#   peer    $PREFIX-peer    $PORT+2  Mare: stock, remoto-jellymesh
# The older builds come from their tags with git archive, built in the SDK container of the project.
# label: a 0.5 server whose label is over 40 bytes keeps it while the migration waits, refuses a rename, and has its stubs
# renamed once at the first start after the migration ends, with the watch state kept: the whole row of each stub, not
# watched and a count with no date included, over the row of 0.4 that Jellyfin reattaches by key (§10 rows 556, 564-567, 572).
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-f42] [PORT=8290] [CASES="auto manual flat half label"] folders.sh
# The containers are stopped at the end, not removed; the next run makes them again.
set -euo pipefail
PREFIX=${PREFIX:-jm-f42}
PORT=${PORT:-8290}
CASES=${CASES:-auto manual flat half label}
CASA_NAME=Casa
# Within the 40 characters of 0.5 but over 40 bytes, so that the cut of row 564 falls inside a character.
LONG_LABEL="JM Домашний медиасервер Антонио"
SHORT_LABEL="JM Домашний медиасерве"
V04=v0.4.3.0
V05=v0.5.0.2
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"
trap 'docker stop "$PREFIX-local" "$PREFIX-remote" "$PREFIX-peer" >/dev/null 2>&1 || true' EXIT

# build <tag|current>: builds that plugin once in the SDK container; the dll, meta.json and version go to $WORK/plugin-<name>.
build() {
  local name=$1 dir=$WORK/plugin-$1 main src yaml version abi out=/tmp/$PREFIX-plugin-$1
  if [ "$name" = current ]; then
    main=$(git -C "$WT" worktree list | head -1 | awk '{print $1}')
    src=/src${WT#"$main"}
    yaml=$(cat "$WT/build.yaml")
  else
    src=/tmp/$PREFIX-src-$name
    git -C "$WT" archive "$name" | docker exec -i jellymesh-sdk sh -c "rm -rf $src && mkdir -p $src && tar -x -C $src"
    yaml=$(git -C "$WT" show "$name:build.yaml")
  fi
  version=$(sed -n 's/^version: "\(.*\)"$/\1/p' <<<"$yaml")
  abi=$(sed -n 's/^targetAbi: "\(.*\)"$/\1/p' <<<"$yaml")
  docker exec -w "$src" jellymesh-sdk sh -c "rm -rf $out && dotnet build Jellyfin.Plugin.JellyMesh/Jellyfin.Plugin.JellyMesh.csproj -c Release -o $out --nologo -v quiet" >/dev/null
  rm -rf "$dir" && mkdir -p "$dir"
  docker cp "jellymesh-sdk:$out/Jellyfin.Plugin.JellyMesh.dll" "$dir/" >/dev/null
  jq -n --arg v "$version" --arg abi "$abi" --arg guid "$PLUGIN" '{guid: $guid, name: "JellyMesh", version: $v, targetAbi: $abi,
    status: "Active", autoUpdate: false, assemblies: ["Jellyfin.Plugin.JellyMesh.dll"]}' >"$dir/meta.json"
  echo "$version" >"$dir/version"
}

# install <tag|current>: puts that build in place of the plugin of the local server and restarts it.
install() {
  local dir=$WORK/plugin-$1 target
  target=/config/plugins/JellyMesh_$(cat "$dir/version")
  docker exec "$PREFIX-local" sh -c 'rm -rf /config/plugins/JellyMesh_* && mkdir -p "$1"' sh "$target"
  docker cp "$dir/Jellyfin.Plugin.JellyMesh.dll" "$PREFIX-local:$target/" >/dev/null
  docker cp "$dir/meta.json" "$PREFIX-local:$target/" >/dev/null
  docker restart "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
}

# old_import_all <server id>: every library of a server, with the routes of 0.4 and 0.5.
old_import_all() {
  local ids
  ids=$(get local "/JellyMesh/Admin/Servers/$1/Libraries" | jq -c '{LibraryIds: map(.Id)}')
  post local "/JellyMesh/Admin/Servers/$1/Libraries" "$ids" >/dev/null
}

migration() { get local /JellyMesh/Admin/Status | jq -c '.FederatedFolder.Migration'; }
step() { migration | jq -r '.Step // "none"'; }
choose() { post local /JellyMesh/Admin/Migration "{\"Mode\":\"$1\"}" >/dev/null; status; }
# wait_run: until an automatic run stops, scanning when it waits at the carry for the new stubs.
wait_run() {
  local i now
  for i in $(seq 1 90); do
    now=$(migration)
    [ "$now" = null ] && return 0
    if [ "$(jq -r .Running <<<"$now")" = false ]; then
      [ "$(jq -r .Error <<<"$now")" != None ] && return 0
      [ "$(jq -r .AutomaticStep <<<"$now")" = Carry ] && scan local
    fi
    sleep 5
  done
  echo "the automatic migration did not stop" >&2
  return 1
}
# scan_until <step>: scans until the banner shows that step.
scan_until() {
  local i
  for i in 1 2 3 4 5 6; do
    scan local
    [ "$(step)" = "$1" ] && return 0
    sleep 5
  done
}
locations() { get local /Library/VirtualFolders | jq -c --arg n "$1" '[.[] | select(.Name == $n) | .Locations[]] | sort'; }
library_id() { get local /Library/VirtualFolders | jq -r --arg n "$1" '.[] | select(.Name == $n) | .ItemId'; }
# on_type_folders <library>: how many of its paths are type folders of a remote library, how many are not, and whether
# they cover each server.
on_type_folders() {
  locations "$1" | jq -r --arg c "$(server_folder "$CASA")" --arg m "$(server_folder "$MARE")" '
    def new: test("^/federata/[0-9a-f-]{36}/[0-9a-f]{32}/[^/]+$");
    "\(map(select(new)) | length) \(map(select(new | not)) | length) \(any(.[]; startswith($c + "/"))) \(any(.[]; startswith($m + "/")))"'
}
# old_stubs: the stubs left in the flat folders and in the folders of the servers of 0.5.
old_stubs() { docker exec "$PREFIX-local" sh -c "find /federata/Film /federata/Serie /federata/*/Film /federata/*/Serie -name '*.strm' 2>/dev/null | wc -l" | tr -d ' '; }
data() { get local "/UserItems/$(dashed "$2")/UserData?userId=$1"; }
set_data() { post local "/UserItems/$(dashed "$2")/UserData?userId=$1" "$3" >/dev/null; status; }
# film <server folder or /federata> <title>: the id of a film whose path is in the remote library layout of that server,
# or in the flat folder for /federata.
film() {
  get local '/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path' | jq -r --arg f "$1" --arg t "$2" '
    [.Items[] | select((.Path // "") | (startswith($f + "/") and contains($t)
      and (if $f == "/federata" then startswith("/federata/Film/") else test("/[0-9a-f]{32}/") end)))][0].Id // ""'
}
# in_library <library> <title>: how many films of that title a library lists.
in_library() {
  get local "/Items?userId=$ME&ParentId=$(library_id "$1")&Recursive=true&IncludeItemTypes=Movie" | jq -r --arg t "$2" '
    [.Items[] | select(.Name | contains($t))] | length'
}
# film_folders <server id>: the film type folders of a server, as locations prints them.
film_folders() {
  docker exec "$PREFIX-local" sh -c "ls -d '$(server_folder "$1")'/*/Film 2>/dev/null" | jq -R -s -c 'split("\n") | map(select(length > 0)) | sort'
}
# copies <title>: how many films of that title the Film library lists, and how many stubs of it Casa has.
copies() {
  get local "/Items?userId=$ME&ParentId=$(library_id Film)&Recursive=true&IncludeItemTypes=Movie" | jq -r --arg t "$1" '
    [.Items[] | select(.Name | contains($t))] | length' | tr -d '\n'
  echo " $(docker exec "$PREFIX-local" sh -c "find '$(server_folder "$CASA")' -name '*.strm' | grep -c '$1' || true")"
}

# fresh_04: a new local server on 0.4 with the flat layout, its libraries on the old paths, both servers synced, and the
# watch state of alice on two flat films.
fresh_04() {
  local i tears sintel
  start local "$PREFIX-media:/media"
  library local Film movies /federata/Film
  library local Serie tvshows /federata/Serie
  ALICE=$(user local alice)
  ME=$(get local /Users/Me | jq -r .Id)
  install "$V04"
  old_settings local '.FederatedFolderPath = "/federata"' >/dev/null
  CASA=$(add_remote local "$CASA_NAME" "http://host.docker.internal:$((PORT + 1))" jellymesh)
  MARE=$(add_remote local Mare "http://host.docker.internal:$((PORT + 2))" jellymesh)
  old_import_all "$CASA"
  old_import_all "$MARE"
  sync_now local
  for i in 1 2 3 4 5; do
    scan local
    [ -n "$(film /federata "Tears of Steel")" ] && [ -n "$(film /federata Sintel)" ] && break
    sleep 10
  done
  tears=$(film /federata "Tears of Steel")
  sintel=$(film /federata Sintel)
  check "0.4 has the flat stubs" "true true" "$([ -n "$tears" ] && echo true || echo false) $([ -n "$sintel" ] && echo true || echo false)"
  check "alice has watched Tears of Steel once and likes it" 200 "$(set_data "$ALICE" "$tears" '{"Played":true,"PlayCount":1,"IsFavorite":true}')"
  check "alice is in the middle of Sintel" 200 "$(set_data "$ALICE" "$sintel" '{"PlaybackPositionTicks":200000000}')"
}

# to_05 <Automatic|Manual> [half]: the upgrade to 0.5 and its migration to the server folders, done as the mode says, or
# stopped by a server folder that cannot join its library with half.
to_05() {
  local id half
  install "$V05"
  sync_now local
  scan local
  check "0.5 asks for the folders of the servers" AddPaths "$(step)"
  if [ "$1" = Automatic ]; then
    if [ "${2:-}" = half ]; then
      half="$(server_folder "$CASA")/Serie"
      docker exec "$PREFIX-local" sh -c "mv '$half' /tmp/serie-away && touch '$half'"
    fi
    check "0.5 migrates on its own" 202 "$(choose Automatic)"
    wait_run
    if [ "${2:-}" = half ]; then
      check "0.5 stops half-way on Serie" "AddFailed Serie" "$(migration | jq -r '"\(.Error) \(.ErrorLibrary)"')"
      docker exec "$PREFIX-local" sh -c "rm '$half' && mv /tmp/serie-away '$half'"
      return 0
    fi
  else
    check "the admin migrates 0.5 by hand" 200 "$(choose Manual)"
    for id in "$CASA" "$MARE"; do
      link_folder local Film "$(server_folder "$id")/Film" >/dev/null
      link_folder local Serie "$(server_folder "$id")/Serie" >/dev/null
    done
    scan_until RemoveOldPaths
    unlink_folder local Film /federata/Film >/dev/null
    unlink_folder local Serie /federata/Serie >/dev/null
  fi
  check "0.5 ends its migration" null "$(migration)"
  check "Film holds the folders of both servers" "$(jq -nc --arg c "$(server_folder "$CASA")/Film" --arg m "$(server_folder "$MARE")/Film" '[$c, $m] | sort')" "$(locations Film)"
}

# casa_libraries: 0.5 makes the libraries of Casa by type, on its server folders.
casa_libraries() {
  local i
  post local "/JellyMesh/Admin/Servers/$CASA/AutomaticLibraries" '{"Enabled":true}' >/dev/null
  check "0.5 makes the libraries of Casa" 202 "$(status)"
  for i in $(seq 1 60); do [ "$(get local /JellyMesh/Admin/Status | jq -r --arg id "$CASA" '.Servers[] | select(.Id == $id) | .AutomaticLibraries.Busy')" = false ] && break; sleep 1; done
  scan local
  CASA_FILM=$(library_id "Casa - Film")
  check "Casa - Film holds the folder of Casa" "[\"$(server_folder "$CASA")/Film\"]" "$(locations "Casa - Film")"
}

# to_06: the upgrade to this build, with a sync and a scan.
to_06() {
  # The log lines before this build, counted rather than timed, since the Docker clock can differ from the host.
  SKIP=$(docker logs "$PREFIX-local" 2>&1 | wc -l)
  install current
  sync_now local
  scan local
}

# by_itself: whether this build ended an automatic migration with no choice of the admin. The banner goes before the
# last scan, so it waits for the end.
by_itself() {
  local logs i
  for i in $(seq 1 60); do
    logs=$(docker logs "$PREFIX-local" 2>&1 | tail -n +$((SKIP + 1)))
    grep -q 'the automatic migration is done' <<<"$logs" && break
    sleep 2
  done
  echo "$(grep -c 'the automatic migration is done' <<<"$logs" || true) $(grep -c 'the admin chose' <<<"$logs" || true)"
}

# restart_local: a new start of the local server.
restart_local() {
  docker restart "$PREFIX-local" >/dev/null
  sleep 3
  wait_ready local
  login local
}
# casa_stubs <text>: how many stubs of Casa have that text in their file name.
casa_stubs() { docker exec "$PREFIX-local" sh -c "find '$(server_folder "$CASA")' -name '*.strm' | sed 's|.*/||' | grep -cF '$1' || true"; }
# short_stubs: how many stubs of Casa have the cut label and not the whole one, which starts with it.
short_stubs() { echo $(($(casa_stubs "$SHORT_LABEL") - $(casa_stubs "$LONG_LABEL"))); }
older_layout() { get local /JellyMesh/Admin/Status | jq -c .OlderLayoutInUse; }
casa_name() { get local /JellyMesh/Admin/Status | jq -r --arg id "$CASA" '.Servers[] | select(.Id == $id) | .Name'; }
# rename_to <name>: the edit form of Casa saved with that name; prints the status, the field and the failure.
rename_to() {
  local body
  body=$(save_server local "$CASA" "$(jq -nc --arg n "$1" '{Name: $n}')")
  echo "$(status) $(jq -r '"\(.Field) \(.Failure)"' <<<"$body" 2>/dev/null)"
}
# renamed_state: the renamed stubs that alice marked not watched or counted with no date keep that row (row 572). Both
# films have provider IDs, so Jellyfin reattaches their rows of 0.4 by key.
renamed_state() {
  local audio multiple
  audio=$(film "$(server_folder "$CASA")" "JM Audio e Sottotitoli")
  multiple=$(film "$(server_folder "$CASA")" "JM Versioni Multiple")
  check "the renamed JM Audio e Sottotitoli stays not watched" "false 0" "$(data "$ALICE" "$audio" | jq -r '"\(.Played) \(.PlayCount)"')"
  check "the renamed JM Versioni Multiple keeps its count with no date" "true 2 null" "$(data "$ALICE" "$multiple" | jq -r '"\(.Played) \(.PlayCount) \(.LastPlayedDate)"')"
}
renamed_lines() { docker logs "$PREFIX-local" 2>&1 | grep -c 'Renamed the stubs of 1 servers whose label was over the limit' || true; }
# page_lock: whether the edit form of Casa on the page locks the name, and its note, in English then Italian.
page_lock() {
  local me sid
  me=$(get local /Users/Me | jq -r .Id)
  sid=$(curl -fsS "http://127.0.0.1:$PORT/System/Info/Public" | jq -r .Id)
  mkdir -p "$WORK/web"
  cp "$S/renamelock.py" "$WORK/web/renamelock.py"
  docker run --rm --add-host host.docker.internal:host-gateway -v "$WORK/web:/w" -v "$PREFIX-pw:/pw" -e PYTHONPATH=/pw "$PW_IMAGE" \
    bash -c 'test -d /pw/playwright || pip install -q --target /pw --no-warn-script-location playwright==1.48.0 2>/dev/null; exec python3 "$@"' _ \
    /w/renamelock.py "http://host.docker.internal:$PORT" "$(token_of local)" "$me" "$sid" "$(casa_name)" | tee "$WORK/web/lock.out"
}
# verify: the end of a migration, with the state of alice on the new stubs.
verify() {
  local tears sintel
  check "the banner is gone" null "$(migration)"
  check "Film holds the film type folders of both servers, Film 4K too, and no old folder" "3 0 true true" "$(on_type_folders Film)"
  check "Serie holds the series type folders of both servers and no old folder" "2 0 true true" "$(on_type_folders Serie)"
  check "no stub is left in the older folders" 0 "$(old_stubs)"
  tears=$(film "$(server_folder "$CASA")" "Tears of Steel")
  sintel=$(film "$(server_folder "$MARE")" Sintel)
  check "Tears of Steel has its state, once though it is in two remote libraries (D19)" "1 true true" "$(data "$ALICE" "$tears" | jq -r '"\(.PlayCount) \(.Played) \(.IsFavorite)"')"
  check "and is one stub in Film" "1 1" "$(copies "Tears of Steel")"
  check "Sintel has its position" 200000000 "$(data "$ALICE" "$sintel" | jq -r .PlaybackPositionTicks)"
}

echo "--- setup: $PREFIX-local on $PORT, $PREFIX-remote on $((PORT + 1)), $PREFIX-peer on $((PORT + 2))"
down
mkdir -p "$WORK"
docker volume create "$PREFIX-media" >/dev/null
build "$V04"
build "$V05"
build current
start remote "$MEDIA/remoto-standard:/media:ro"
library remote Film movies /media/film
library remote "Film 4K" movies /media/film
library remote Serie tvshows /media/serie
user remote jellymesh >/dev/null
scan remote
check "the remote has Film and Film 4K on the same films" '["Film","Film 4K","Serie"]' "$(get remote /Library/VirtualFolders | jq -c '[.[].Name] | sort')"
start peer "$MEDIA/remoto-jellymesh:/media:ro"
library peer Film movies /media/film
library peer Serie tvshows /media/serie
user peer jellymesh >/dev/null
scan peer

for case in $CASES; do
  case $case in
    auto)
      echo "--- from a 0.5 that migrated on its own: the choice is kept and the migration runs again by itself [§10 row 444]"
      fresh_04
      to_05 Automatic
      casa_libraries
      to_06
      check "Casa keeps its libraries by type, Mare has none" "ByType None" "$(get local /JellyMesh/Admin/Status | jq -r --arg c "$CASA" --arg m "$MARE" '"\(.Servers[] | select(.Id == $c) | .AutomaticLibraries.Mode) \(.Servers[] | select(.Id == $m) | .AutomaticLibraries.Mode)"')"
      wait_run
      check "the choice of 0.5 is kept: the migration ends with no choice" "1 0" "$(by_itself)"
      verify
      casa_films=$(locations Film | jq -c --arg c "$(server_folder "$CASA")/" 'map(select(startswith($c)))')
      for i in $(seq 1 30); do [ "$(locations "Casa - Film")" = "$casa_films" ] && break; sleep 2; done
      check "Casa - Film keeps its id" "$CASA_FILM" "$(library_id "Casa - Film")"
      check "and moves to the two film type folders of Casa" "$casa_films" "$(locations "Casa - Film")"
      ;;
    manual)
      echo "--- from a 0.5 migrated by hand: the banner stays manual and the admin moves the paths [§10 row 444]"
      fresh_04
      to_05 Manual
      casa_libraries
      to_06
      check "the choice of 0.5 is kept" "AddPaths Manual" "$(migration | jq -r '"\(.Step) \(.Mode)"')"
      check "the banner lists both libraries on their server folders" "Film,Serie" "$(migration | jq -r '[.Libraries[].Name] | sort | join(",")')"
      check "with the type folders to add" 5 "$(migration | jq '[.Libraries[].MissingPaths[]] | length')"
      casa_films=$(film_folders "$CASA")
      # Until the carry the plugin adds only the type folders that hold titles beside the old folder of Casa.
      beside() { locations "Casa - Film" | jq -r --arg o "$(server_folder "$CASA")/Film" 'index($o) != null and (map(select(test("/[0-9a-f]{32}/Film$"))) | length) > 0'; }
      for i in $(seq 1 30); do [ "$(beside)" = true ] && break; sleep 2; done
      check "until the carry Casa - Film holds the old folder of Casa beside a film type folder" true "$(beside)"
      scan local
      check "so it shows Tears of Steel twice (D61)" 2 "$(in_library "Casa - Film" "Tears of Steel")"
      check "and the banner says so while titles are left to carry" true "$(migration | jq '.OldTitles > 0')"
      while IFS=$'\t' read -r name path; do
        link_folder local "$name" "$path" >/dev/null
      done < <(migration | jq -r '.Libraries[] | .Name as $n | .MissingPaths[] | [$n, .] | @tsv')
      scan_until RemoveOldPaths
      check "the banner asks to remove the old paths" RemoveOldPaths "$(step)"
      for i in $(seq 1 30); do [ "$(locations "Casa - Film")" = "$casa_films" ] && break; sleep 2; done
      check "right after the carry, with no sync, Casa - Film moves to the film type folders of Casa (F45)" "$casa_films" "$(locations "Casa - Film")"
      check "and keeps its id" "$CASA_FILM" "$(library_id "Casa - Film")"
      while IFS=$'\t' read -r name path; do
        unlink_folder local "$name" "$path" >/dev/null
      done < <(migration | jq -r '.Libraries[] | .Name as $n | .OldPaths[] | [$n, .] | @tsv')
      verify
      scan local
      check "Casa - Film shows Tears of Steel once" 1 "$(in_library "Casa - Film" "Tears of Steel")"
      ;;
    flat)
      echo "--- from a 0.4 flat layout: the banner asks, and the automatic choice moves it all [§10 row 445]"
      fresh_04
      to_06
      check "the banner asks for a choice" "AddPaths None" "$(migration | jq -r '"\(.Step) \(.Mode)"')"
      check "for the libraries on the flat folders" '["/federata/Film","/federata/Serie"]' "$(migration | jq -c '[.Libraries[].OldPaths[]] | sort')"
      check "the flat film keeps its state until then" true "$(data "$ALICE" "$(film /federata "Tears of Steel")" | jq -r .IsFavorite)"
      check "the plugin does it" 202 "$(choose Automatic)"
      wait_run
      verify
      ;;
    half)
      echo "--- from a 0.5 automatic migration stopped half-way: both older layouts go to the type folders [§10 row 445]"
      fresh_04
      to_05 Automatic half
      echo "Film: $(locations Film), Serie: $(locations Serie)"
      to_06
      wait_run
      check "the run starts again without the error of 0.5 and ends with no choice" "1 0" "$(by_itself)"
      verify
      ;;
    label)
      echo "--- a 0.5 server with a label over 40 bytes: no rename until the folder migration ends, then once [§10 rows 556, 564-567]"
      CASA_NAME=$LONG_LABEL
      fresh_04
      CASA_NAME=Casa
      audio=$(film /federata "JM Audio e Sottotitoli")
      multiple=$(film /federata "JM Versioni Multiple")
      check "alice has watched JM Audio e Sottotitoli and JM Versioni Multiple once on 0.4" "200 200" "$(set_data "$ALICE" "$audio" '{"Played":true,"PlayCount":1}') $(set_data "$ALICE" "$multiple" '{"Played":true,"PlayCount":1}')"
      to_05 Manual
      to_06
      check "the folder migration waits for the admin" "AddPaths Manual" "$(migration | jq -r '"\(.Step) \(.Mode)"')"
      check "the status says the older layout is in use" true "$(older_layout)"
      check "the stubs of the server carry the label of 0.5" true "$([ "$(casa_stubs "$LONG_LABEL")" -gt 0 ] && echo true || echo false)"
      check "a new name is refused on the name while the migration has not ended (row 566)" "409 Name MigrationUnfinished" "$(rename_to Casa)"
      check "the name is unchanged" "$LONG_LABEL" "$(casa_name)"
      check "the same name still saves" "200" "$(rename_to "$LONG_LABEL" | cut -d' ' -f1)"
      page_lock >/dev/null
      check "the page locks the name, in English and Italian" "true true" "$(awk '$1 == "LOCK" { printf "%s%s", s, $3; s = " " }' "$WORK/web/lock.out")"
      check "and says the migration has to end first" true "$(grep '^LOCK en' "$WORK/web/lock.out" | grep -qi 'migration' && echo true || echo false)"
      restart_local
      sleep 45
      check "a start before the end of the migration renames nothing" "0 0" "$(renamed_lines) $(short_stubs)"
      check "and the stubs keep the label of 0.5" true "$([ "$(casa_stubs "$LONG_LABEL")" -gt 0 ] && echo true || echo false)"

      echo "--- the admin ends the migration: the next start renames the stubs once, with the state kept [§10 rows 564-565]"
      while IFS=$'\t' read -r name path; do
        link_folder local "$name" "$path" >/dev/null
      done < <(migration | jq -r '.Libraries[] | .Name as $n | .MissingPaths[] | [$n, .] | @tsv')
      scan_until RemoveOldPaths
      while IFS=$'\t' read -r name path; do
        unlink_folder local "$name" "$path" >/dev/null
      done < <(migration | jq -r '.Libraries[] | .Name as $n | .OldPaths[] | [$n, .] | @tsv')
      scan local
      check "the banner is gone" null "$(migration)"
      check "the status says the older layout is empty" false "$(older_layout)"
      check "the end of the migration renames nothing by itself" 0 "$(renamed_lines)"
      tears=$(film "$(server_folder "$CASA")" "Tears of Steel")
      omonimo=$(film "$(server_folder "$CASA")" "Titolo Omonimo")
      audio=$(film "$(server_folder "$CASA")" "JM Audio e Sottotitoli")
      multiple=$(film "$(server_folder "$CASA")" "JM Versioni Multiple")
      # Jellyfin reattaches the row of 0.4 to the renamed stub by key: the rename copies the whole row over it (row 572).
      twice=$(printf '{"Played":true,"PlayCount":2,"IsFavorite":true,"LastPlayedDate":"%s"}' "$(date -u +%Y-%m-%dT%H:%M:%SZ)")
      check "alice watches Tears of Steel twice and likes it" 200 "$(set_data "$ALICE" "$tears" "$twice")"
      check "alice is in the middle of Titolo Omonimo" 200 "$(set_data "$ALICE" "$omonimo" '{"PlaybackPositionTicks":300000000}')"
      check "alice marks JM Audio e Sottotitoli not watched" 200 "$(set_data "$ALICE" "$audio" '{"Played":false,"PlayCount":0}')"
      check "alice counts JM Versioni Multiple twice with no date" 200 "$(set_data "$ALICE" "$multiple" '{"Played":true,"PlayCount":2}')"
      check "so JM Audio e Sottotitoli is not watched" "false 0" "$(data "$ALICE" "$audio" | jq -r '"\(.Played) \(.PlayCount)"')"
      check "and JM Versioni Multiple watched twice with no date" "true 2 null" "$(data "$ALICE" "$multiple" | jq -r '"\(.Played) \(.PlayCount) \(.LastPlayedDate)"')"
      long=$(casa_stubs "$LONG_LABEL")
      restart_local
      for i in $(seq 1 90); do
        [ "$(renamed_lines)" -ge 1 ] && break
        sleep 4
      done
      check "the first start after the migration renames the stubs once" 1 "$(renamed_lines)"
      check "no stub keeps the label of 0.5" 0 "$(casa_stubs "$LONG_LABEL")"
      check "each stub takes the label cut to 40 bytes" "$long" "$(short_stubs)"
      check "the label fits in 40 bytes" true "$([ "$(printf '%s' "$SHORT_LABEL" | wc -c)" -le 40 ] && echo true || echo false)"
      scan local
      tears=$(film "$(server_folder "$CASA")" "Tears of Steel")
      omonimo=$(film "$(server_folder "$CASA")" "Titolo Omonimo")
      check "the renamed Tears of Steel keeps the state of alice" "2 true true" "$(data "$ALICE" "$tears" | jq -r '"\(.PlayCount) \(.Played) \(.IsFavorite)"')"
      check "and Titolo Omonimo its position" 300000000 "$(data "$ALICE" "$omonimo" | jq -r .PlaybackPositionTicks)"
      renamed_state
      check "the name of the server is unchanged" "$LONG_LABEL" "$(casa_name)"
      before=$(docker exec "$PREFIX-local" sh -c "find '$(server_folder "$CASA")' -name '*.strm' | sort | md5sum")
      restart_local
      sleep 45
      check "a later start renames nothing again" 1 "$(renamed_lines)"
      check "and leaves the stubs where they are" "$before" "$(docker exec "$PREFIX-local" sh -c "find '$(server_folder "$CASA")' -name '*.strm' | sort | md5sum")"
      renamed_state
      ;;
  esac
done
check "the status names no remote address" 0 "$(get local /JellyMesh/Admin/Status | jq -c '.FederatedFolder' | grep -c "host.docker.internal" || true)"
exit "$FAILED"
