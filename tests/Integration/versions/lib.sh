# Helpers for the proof of linked versions (F07). Source it. Builds on play/lib.sh:
#   local   $PREFIX-local   $PORT    plugin, writable /media, Nfo saver on, consumer of both remotes
#   remote  $PREFIX-remote  $PORT+1  stock Jellyfin with the media of remoto-standard
#   peer    $PREFIX-peer    $PORT+2  stock Jellyfin with the media of remoto-jellymesh
# PREFIX (default jm-versions) and PORT (default 18500) move it all elsewhere. Reads the database with sqlite3.
PREFIX=${PREFIX:-jm-versions}
PORT=${PORT:-18500}
source "$(dirname "${BASH_SOURCE[0]}")/../play/lib.sh"

# db: a copy of the database of the local server, with its write-ahead log.
db() {
  docker cp "$PREFIX-local:/config/data/jellyfin.db" "$WORK/jf.db" >/dev/null 2>&1
  rm -f "$WORK/jf.db-wal" "$WORK/jf.db-shm"
  docker cp "$PREFIX-local:/config/data/jellyfin.db-wal" "$WORK/jf.db-wal" >/dev/null 2>&1 || true
  echo "$WORK/jf.db"
}
sql() { sqlite3 "$(db)" "$1"; }

# vid <path substring>: the N-format id of the video whose path has it. Lists hide linked alternates.
vid() {
  sql "select lower(replace(Id, '-', '')) from BaseItems where Path like '%' || '$1' || '%'
    and (Type like '%.Movie' or Type like '%.Episode' or Type like '%.Video') order by Path limit 1;"
}
# primary_of <id>: the PrimaryVersionId of an item, empty if none.
primary_of() { sql "select coalesce(lower(replace(PrimaryVersionId, '-', '')), '') from BaseItems where lower(replace(Id, '-', '')) = '$1';"; }
# alternates_of <id>: its linked alternates, sorted, comma separated.
alternates_of() {
  sql "select lower(replace(ChildId, '-', '')) from LinkedChildren where lower(replace(ParentId, '-', '')) = '$1' and ChildType = 3
    order by 1;" | paste -sd, -
}
# saved <id>...: when each item was last saved.
saved() { local i; for i in "$@"; do sql "select DateLastSaved from BaseItems where lower(replace(Id, '-', '')) = '$i';"; done | paste -sd' ' -; }
# children_of <container name>: the item ids of a collection or playlist, in order.
children_of() {
  sql "select lower(replace(l.ChildId, '-', '')) from LinkedChildren l join BaseItems b on b.Id = l.ParentId
    where b.Name = '$1' order by l.SortOrder;" | paste -sd, -
}
# nfo_sums: a checksum of every .nfo in the media and federated folders.
nfo_sums() { docker exec "$PREFIX-local" sh -c "find /media /federata -name '*.nfo' -exec md5sum {} + | sort -k2"; }
# stub_of <server id> <title>: the id of the stub of a title in the folder of a server.
stub_of() { vid "$(server_folder "$1")/Film/$2"; }
dashed() { local id=$1; echo "${id:0:8}-${id:8:4}-${id:12:4}-${id:16:4}-${id:20:12}"; }

DIRECT='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Type":"Video","Container":"mkv,mp4,webm","VideoCodec":"h264,hevc,av1,vp9","AudioCodec":"aac,ac3,eac3,mp3,opus,flac,vorbis"}],"TranscodingProfiles":[]}}'
# reconcile: runs the nightly reconciliation, which links thoroughly, and waits for its sync to end.
reconcile() {
  local id last new i
  id=$(get local /ScheduledTasks | jq -r '.[] | select(.Key == "JellyMeshReconciliation") | .Id')
  last=$(get local "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
  post local "/ScheduledTasks/Running/$id" >/dev/null
  for i in $(seq 1 300); do
    new=$(get local "/ScheduledTasks/$id" | jq -r '.LastExecutionResult.EndTimeUtc // ""')
    [ "$new" != "$last" ] && [ "$(get local /JellyMesh/Admin/Sync | jq -r .IsRunning)" = false ] && return 0
    sleep 1
  done
  echo "the reconciliation did not end" >&2
  return 1
}
# film_links <path substring>: OwnerId, PrimaryVersionId and linked children of the items under a path, by path.
film_links() {
  sql "select b.Path || ' owner=' || coalesce(b.OwnerId, '') || ' primary=' || coalesce(p.Path, '') from BaseItems b
    left join BaseItems p on p.Id = b.PrimaryVersionId where b.Path like '%$1%' order by b.Path;"
  sql "select b.Path || ' -> ' || coalesce(c.Path, l.ChildId) || ' type=' || l.ChildType from LinkedChildren l
    join BaseItems b on b.Id = l.ParentId left join BaseItems c on c.Id = l.ChildId where b.Path like '%$1%' order by 1;"
}
# put_film <media folder> <folder on the local server>: copies a film of the test media into /media/film.
put_film() {
  docker exec "$PREFIX-local" mkdir -p "/media/film/$2"
  docker cp "$MEDIA/$1/." "$PREFIX-local:/media/film/$2/"
  docker exec "$PREFIX-local" sh -c 'chmod -R a+rwX /media/film'
}
