#!/usr/bin/env bash
# Integration 2 of the 0.6 spec (F27, §10 rows 426, 469): the data of a 0.5 and of a 0.4 moves into the plugin database.
# Each case sets a consumer up on the real older build, from its tag: two servers with saved passwords, a connected
# JellyMesh server, a manual merge, a changed setting and, on 0.5, the watch state kept from a removed server. After the
# upgrade to this build the same servers, connected server, merge and setting are there, a sync logs in with the saved
# passwords, the old files are kept in migrated-0.5 for 30 days, and the kept watch state comes back with the server.
#   local   $PREFIX-local   $PORT    plugin, user alice, connected server user mesh
#   remote  $PREFIX-remote  $PORT+1  stock, Film of remoto-standard and /extra
#   peer    $PREFIX-peer    $PORT+2  stock, Film of remoto-jellymesh and /extra
#   gone    $PREFIX-gone    $PORT+3  stock, Serie of remoto-standard: removed on 0.5 and added back after the upgrade
# usage: MEDIA=<main checkout>/tests/Integration/media [PREFIX=jm-data-migration] [PORT=18540] [CASES="v0.5.0.2 v0.4.3.0"]
#   data-migration.sh   (KEEP=1 leaves the last case up)
set -euo pipefail
PREFIX=${PREFIX:-jm-data-migration}
PORT=${PORT:-18540}
CASES=${CASES:-v0.5.0.2 v0.4.3.0}
source "$(dirname "${BASH_SOURCE[0]}")/../merge/lib.sh"

port_of() { case $1 in local | alice) echo "$PORT" ;; remote) echo $((PORT + 1)) ;; peer) echo $((PORT + 2)) ;; gone) echo $((PORT + 3)) ;; esac; }
down() {
  local s
  for s in local remote peer gone; do
    docker rm -f "$PREFIX-$s" >/dev/null 2>&1 || true
    docker volume rm "$PREFIX-$s-config" "$PREFIX-$s-cache" "$PREFIX-$s-fed" "$PREFIX-$s-extra" >/dev/null 2>&1 || true
  done
  docker volume rm "$PREFIX-media" "$PREFIX-pw" >/dev/null 2>&1 || true
}
[ "${KEEP:-}" = 1 ] || trap down EXIT

SD="$MEDIA/remoto-standard/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"
HD="$MEDIA/remoto-jellymesh/film/Sonda Senza ID (2026)/Sonda Senza ID (2026).mkv"

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
in_local() { docker exec "$PREFIX-local" sh -c "$1"; }
servers() { get local /JellyMesh/Admin/Status | jq -c '[.Servers[] | {Id, Name}] | sort_by(.Name)'; }
peers() { get local /JellyMesh/Admin/Peers | jq -c '[.[] | {UserId, UserName, SharedLibraryIds, MaxStreams}]'; }
merges() { list local | jq -c '[.Merges[] | {Id, Titles: ([.Copies[] | "\(.Copy.Title) \(.ServerName)"] | sort)}]'; }
# episode: the first federated episode alice sees, with its watch state.
episode() {
  get alice '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path&SortBy=Path' |
    jq -c '[.Items[] | select((.Path // "") | startswith("/federata/")) | {Id, Key: "\(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber)", Played: .UserData.Played}][0] // {}'
}
# played <key>: whether alice sees an episode of that key as played.
played() {
  get alice '/Items?Recursive=true&IncludeItemTypes=Episode&Fields=Path' |
    jq -r --arg k "$1" '[.Items[] | select((.Path // "") | startswith("/federata/")) | select("\(.SeriesName) S\(.ParentIndexNumber)E\(.IndexNumber)" == $k) | .UserData.Played] | any'
}

run_case() {
  local tag=$1 a b c peer_uid lib before_servers before_peers before_merges ep key i
  echo "=== $tag"
  down
  mkdir -p "$WORK"
  docker volume create "$PREFIX-media" >/dev/null
  for s in remote peer; do
    START_ENV="-v $PREFIX-$s-extra:/extra" start "$s" "$MEDIA/$([ "$s" = remote ] && echo remoto-standard || echo remoto-jellymesh):/media:ro"
    docker exec "$PREFIX-$s" sh -c 'mkdir -p /extra/film && chmod a+rwx /extra/film'
    library "$s" Film movies /media/film /extra/film
    user "$s" jellymesh >/dev/null
  done
  add_clip remote "$SD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Un pianista insegue un sogno tra le colline."
  add_clip peer "$HD" "JM Sonda Mobile (2026)" "JM Sonda Mobile" "" "" "Una spedizione polare perde i contatti con la base."
  scan remote
  scan peer
  if [ "$tag" != v0.4.3.0 ]; then
    start gone "$MEDIA/remoto-standard:/media:ro"
    library gone Serie tvshows /media/serie
    user gone jellymesh >/dev/null
    scan gone
  fi

  start local "$PREFIX-media:/media"
  docker exec "$PREFIX-local" sh -c 'mkdir -p /media/film && touch /media/film/.keep'
  library local Film movies /media/film
  user local alice >/dev/null
  peer_uid=$(user local mesh)
  install "$tag"
  login alice alice
  old_settings local '.FederatedFolderPath = "/federata" | .SyncIntervalSeconds = 1200' >/dev/null
  a=$(add_remote local Casa "http://host.docker.internal:$(port_of remote)" jellymesh)
  b=$(add_remote local Mare "http://host.docker.internal:$(port_of peer)" jellymesh)
  old_import_all "$a"
  old_import_all "$b"
  lib=$(get local /JellyMesh/Admin/Peers/Options | jq -r '.Libraries[] | select(.Name == "Film") | .Id')
  req local PUT "/JellyMesh/Admin/Peers/$peer_uid" "$(jq -n --arg l "$lib" '{SharedLibraryIds: [$l], MaxStreams: 2}')" >/dev/null
  check "$tag connects the server of mesh" true "$([ "$(status)" -lt 300 ] && echo true || echo false)"
  sync_now local
  check "$tag merges the pair without IDs" 204 "$(merge local '^JM Sonda Mobile$')"

  if [ "$tag" != v0.4.3.0 ]; then
    c=$(add_remote local Monte "http://host.docker.internal:$(port_of gone)" jellymesh)
    old_import_all "$c"
    sync_now local
    library local Serie tvshows "$(server_folder "$c")/Serie"
    for i in 1 2 3 4 5; do
      scan local
      [ "$(episode)" != '{}' ] && break
      sleep 5
    done
    ep=$(episode)
    key=$(jq -r .Key <<<"$ep")
    post alice "/UserPlayedItems/$(jq -r .Id <<<"$ep")" >/dev/null
    check "$tag: alice watched $key" true "$(played "$key")"
    req local DELETE "/JellyMesh/Admin/Servers/$c" >/dev/null
    check "$tag removes Monte" 204 "$(status)"
    for i in $(seq 1 60); do
      [ "$(in_local "find /federata -path '*/Serie/*' -name '*.strm' | wc -l" | tr -d ' ')" = 0 ] && break
      sleep 1
    done
    check "$tag keeps the watch state in watch-state.db" there "$(in_local "test -s $DATA/watch-state.db && echo there || echo gone")"
  fi

  before_servers=$(servers)
  before_peers=$(peers)
  before_merges=$(merges)
  check "$tag has two servers" 2 "$(jq length <<<"$before_servers")"
  check "$tag has the connected server" mesh "$(jq -r '.[0].UserName' <<<"$before_peers")"
  check "$tag has the merge" 1 "$(jq length <<<"$before_merges")"
  check "$tag keeps its settings in the XML" there "$(in_local "test -s '$OLD_XML' && echo there || echo gone")"
  old_files=$(in_local "cd $DATA && ls -1 *.json watch-state.db 2>/dev/null | sort" | paste -sd' ' -)
  echo "files of $tag: $old_files"

  echo "--- $tag to this build"
  install current
  check "the database is healthy" Healthy "$(get local /JellyMesh/Admin/Store | jq -r .State)"
  check "the old files are kept for 30 days" true "$(get local /JellyMesh/Admin/Store | jq '.MigratedKeptUntil != null and
    ((.MigratedKeptUntil[0:19] + "Z" | fromdateiso8601) - now | . / 86400 | floor | . == 29 or . == 30)')"
  check "the status says so too" true "$(get local /JellyMesh/Admin/Status | jq '.Store.MigratedKeptUntil != null')"
  check "the same servers" "$before_servers" "$(servers)"
  check "the same connected server" "$before_peers" "$(peers)"
  check "the same merge" "$before_merges" "$(merges)"
  check "the pair is no case again" 0 "$(list local | jq '[.Cases[].Titles[].Copies[] | select(.Copy.Title == "JM Sonda Mobile")] | length')"
  check "the setting is kept" 1200 "$(get local /JellyMesh/Admin/Settings | jq -r .SyncIntervalSeconds)"
  check "and the folder" /federata "$(get local /JellyMesh/Admin/Settings | jq -r .FederatedFolderPath)"
  check "no server asks for its password" false "$(get local /JellyMesh/Admin/Status | jq '[.Servers[].NeedsPassword] | any')"
  check "a sync starts" 202 "$(post local /JellyMesh/Admin/Sync >/dev/null; status)"
  sync_now local
  check "it logs in to both servers with the saved passwords" true "$(get local /JellyMesh/Admin/Sync | jq '([.Servers[] | .Error == null] | all) and ([.Servers[].Movies > 0] | all)')"
  check "the XML left the configurations folder" gone "$(in_local "test -e '$OLD_XML' && echo there || echo gone")"
  check "the XML is in migrated-0.5" there "$(in_local "test -s '$DATA/migrated-0.5/Jellyfin.Plugin.JellyMesh.xml' && echo there || echo gone")"
  check "the old files are in migrated-0.5" "$old_files" "$(in_local "cd $DATA/migrated-0.5 && ls -1 *.json watch-state.db 2>/dev/null | sort" | paste -sd' ' -)"
  check "none is left beside the database" 0 "$(in_local "ls $DATA/*.json $DATA/watch-state.db 2>/dev/null | wc -l" | tr -d ' ')"
  check "migrated-0.5 is its owner's only" drwx------ "$(in_local "ls -ld $DATA/migrated-0.5 | cut -c1-10")"
  check "the saved passwords are in the database, encrypted" 2 "$(db_query local "SELECT COUNT(*) FROM servers WHERE length(encrypted_credentials) > 0;")"
  check "no saved password is in the clear" 0 "$(docker run --rm -v "$PREFIX-local-config:/config:ro" --entrypoint sh "$IMAGE" -c "grep -c jellymesh-test $DATA/jellymesh.db || true")"

  if [ "$tag" != v0.4.3.0 ]; then
    check "the kept watch state is in the database" true "$([ "$(db_query local 'SELECT COUNT(*) FROM kept_titles;')" -gt 0 ] && echo true || echo false)"
    c=$(add_remote local Monte "http://host.docker.internal:$(port_of gone)" jellymesh)
    import_all local "$c"
    sync_now local
    for f in $(in_local "ls -d '$(server_folder "$c")'/*/Serie"); do link_folder local Serie "$f" >/dev/null; done
    for i in 1 2 3 4 5 6; do
      scan local
      [ "$(played "$key")" = true ] && break
      sleep 5
    done
    check "alice's $key is played again with Monte back" true "$(played "$key")"
    check "the kept watch state is forgotten once put back" 0 "$(db_query local 'SELECT COUNT(*) FROM kept_titles;')"
  fi
  check "the move is logged" 1 "$(docker logs "$PREFIX-local" 2>&1 | grep -cm1 'The data of the older JellyMesh version moved into the plugin database' || true)"
  check "no load or old file error is logged" 0 \
    "$(docker logs "$PREFIX-local" 2>&1 | grep -cE 'FileLoadException|TypeInitializationException|DllNotFoundException|MissingMethodException|The old JellyMesh (file|configuration|files).*(cannot|damaged)' || true)"
}

mkdir -p "$WORK"
build current
for tag in $CASES; do build "$tag"; done
for tag in $CASES; do run_case "$tag"; done
exit "$FAILED"
