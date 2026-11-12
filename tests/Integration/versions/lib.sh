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

sync_scan_sync() { sync_now local; scan local; sync_now local; }
