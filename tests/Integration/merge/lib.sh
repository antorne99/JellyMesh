# Helpers for the proof of decision 20, on private servers. Source it. Reuses the helpers of dedup/ under other names:
#   stock  jm-merge-s  18442  media of remoto-standard   no plugin
#   peer   jm-merge-j  18443  media of remoto-jellymesh  plugin, shares its libraries
#   fresh  jm-merge-f  18441  media of locale            plugin, consumer of both
# They share the network jm-merge-net. Never touches the jellymesh-test stack.
WORK=${WORK:-${TMPDIR:-/tmp}/jm-merge-work}
source "$(dirname "${BASH_SOURCE[0]}")/../dedup/lib.sh"
NETWORK=jm-merge-net
PW_IMAGE=mcr.microsoft.com/playwright/python:v1.48.0-noble
AUTH='MediaBrowser Client="JellyMeshMerge", Device="merge", DeviceId="jm-merge-admin", Version="0.1"'

port_of() { case $1 in fresh) echo 18441 ;; stock) echo 18442 ;; peer) echo 18443 ;; esac; }
container_of() { case $1 in fresh) echo jm-merge-f ;; stock) echo jm-merge-s ;; peer) echo jm-merge-j ;; esac; }

# add_clip <server> <clip> <folder> <title> <tmdb> <imdb> <plot>: a film in /extra/film, with its .nfo. Empty IDs are left out.
add_clip() {
  local name folder=/extra/film/$3 file=$3 nfo
  name=$(container_of "$1")
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
merge() { post "$1" /JellyMesh/Admin/Merges "$(keys_of "$1" "$2")" >/dev/null; cat "$WORK/status"; }
merge_id() { list "$1" | jq -r --arg t "$2" '[.Merges[] | select(any(.Copies[]; .Copy.Title | test($t))) | .Id] | first'; }
undo() { req "$1" DELETE "/JellyMesh/Admin/Merges/$(merge_id "$1" "$2")" >/dev/null; cat "$WORK/status"; }

# A merge reaches the library with the scan it queues. One more makes sure nothing was waiting for the first.
applied() { settle "$1"; scan "$1"; settle "$1"; }

film_by_path() { items "$1" Movie | jq -r --arg p "$2" '[.Items[] | select(.Path | contains($p)) | .Id] | first'; }
state() { # state <server> <item>: what a client sees of the watch state
  get "$1" "/Users/$(admin_id "$1")/Items/$2" | jq -c '{Played: .UserData.Played, PlayCount: .UserData.PlayCount, Favorite: .UserData.IsFavorite}'
}
strm_count() { stubs "$1" | grep -c "$2" || true; }
