# Helpers for the proof of manual merges as versions (decision 20, Q23), on private servers. Source it. Builds on
# versions/lib.sh:
#   local   $PREFIX-local   $PORT    plugin, writable /media, consumer of both remotes
#   remote  $PREFIX-remote  $PORT+1  stock Jellyfin, media of remoto-standard, writable /extra
#   peer    $PREFIX-peer    $PORT+2  stock Jellyfin, media of remoto-jellymesh, writable /extra
# PREFIX (default jm-merge) and PORT (default 18441) move it all elsewhere. Never touches the jellymesh-test stack.
PREFIX=${PREFIX:-jm-merge}
PORT=${PORT:-18441}
source "$(dirname "${BASH_SOURCE[0]}")/../versions/lib.sh"

# add_clip <server> <clip> <folder> <title> <tmdb> <imdb> <plot>: a film in /extra/film, with its .nfo. Empty IDs are left out.
add_clip() {
  local name folder=/extra/film/$3 file=$3 nfo
  name=$(name_of "$1")
  nfo="<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?><movie><title>$4</title><year>2026</year><plot>$7</plot>"
  [ -n "$5" ] && nfo+="<uniqueid type=\"tmdb\" default=\"true\">$5</uniqueid>"
  [ -n "$6" ] && nfo+="<uniqueid type=\"imdb\">$6</uniqueid>"
  nfo+="</movie>"
  docker exec "$name" mkdir -p "$folder"
  docker cp "$2" "$name:$folder/$file.mkv" >/dev/null
  printf '%s' "$nfo" | docker exec -i "$name" sh -c "cat >'$folder/movie.nfo'"
  docker exec "$name" chmod -R a+rwX "$folder"
}

list() { get "$1" /JellyMesh/Admin/Merges; }
# The keys of the titles whose copies have a title matching a regex, as a request.
keys_of() { list "$1" | jq -c --arg t "$2" '{WorkKeys: [.Cases[].Titles[] | select(any(.Copies[]; .Copy.Title | test($t))) | .WorkKey]}'; }
merge() { post "$1" /JellyMesh/Admin/Merges "$(keys_of "$1" "$2")" >/dev/null; status; }
merge_id() { list "$1" | jq -r --arg t "$2" '[.Merges[] | select(any(.Copies[]; .Copy.Title | test($t))) | .Id] | first'; }
undo() { req "$1" DELETE "/JellyMesh/Admin/Merges/$(merge_id "$1" "$2")" >/dev/null; status; }

scan_state() { get "$1" /ScheduledTasks | jq -r '.[] | select(.Key == "RefreshLibrary") | .State'; }
# settle <server>: waits for a scan the plugin queued, if any.
settle() {
  local i
  sleep 3
  for i in $(seq 1 180); do
    [ "$(scan_state "$1")" = Idle ] && { sleep 2; return 0; }
    sleep 1
  done
  echo "scan of $1 did not settle" >&2
  return 1
}
# A merge reaches the library with the scan it queues. One more makes sure nothing was waiting for the first.
applied() { settle "$1"; scan "$1"; settle "$1"; }

# film_of <server> <title>: the id of the stub of a title in the folder of a remote server, hidden alternates included.
film_of() { stub_of "$(cat "$WORK/id-$1")" "$2"; }
# strm_count <title>: the .strm files of a title in every server folder.
strm_count() { docker exec "$PREFIX-local" sh -c "find /federata/*/Film -name '*.strm' | grep -cF \"$1\"" || true; }
# rows <title>: the film rows a client sees for a title.
rows() { get local "/Items?userId=$uid&Recursive=true&IncludeItemTypes=Movie&searchTerm=$(jq -rn --arg t "$1" '$t | @uri')" | jq --arg t "$1" '[.Items[] | select(.Name | startswith($t))] | length'; }
# state <item>: what a client sees of the watch state of the admin.
state() { get local "/Users/$uid/Items/$(dashed "$1")" | jq -c '{Played: .UserData.Played, PlayCount: .UserData.PlayCount, Favorite: .UserData.IsFavorite}'; }
# group <item>: the primary and the alternates of an item, so that a link reads the same from either side.
group() {
  local p
  p=$(primary_of "$1")
  [ -z "$p" ] && p=$1
  echo "$p:$(alternates_of "$p")"
}

merge_down() {
  down
  docker volume rm "$PREFIX-remote-extra" "$PREFIX-peer-extra" >/dev/null 2>&1 || true
}
